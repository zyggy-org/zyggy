using System.Globalization;

using Microsoft.Extensions.Logging;

using Zyggy.Core.Envelope;
using Zyggy.Core.Memory;
using Zyggy.Core.Tenancy;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Bus;

/// <summary>
/// Central's pickup (spec 13; founding spec §7 step 1 as amended by A13-3/A13-10): after a pull, every verified context and
/// report another machine of the tenant addressed to Central reaches the memory inbox — a context's body verbatim minus
/// secret-shaped lines, a report as one <c>[observed]</c> line — and is archived in one commit. Never calls a model, never runs
/// git in the memory repository, never throws to the loop except on cancellation.
/// </summary>
public sealed partial class BusPickup
{
    private readonly BusRepository _bus;
    private readonly EnvelopeSigner _signer;
    private readonly PickupInbox _inbox;
    private readonly SecretPatterns _patterns;
    private readonly ILogger<BusPickup> _log;
    private readonly HashSet<string> _warnedLeft = new(StringComparer.Ordinal);
    private bool _archiveFailureLogged;
    private bool _inboxFailureLogged;

    internal BusPickup(BusRepository bus, EnvelopeSigner signer, PickupInbox inbox, SecretPatterns patterns, ILogger<BusPickup> log)
    {
        _bus = bus;
        _signer = signer;
        _inbox = inbox;
        _patterns = patterns;
        _log = log;
    }

    /// <summary>Runs one pass: list, verify, copy to the inbox, archive in one commit, log once.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>What the pass did; an unexpected failure is logged once and gives an empty result.</returns>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="ct"/> is cancelled.</exception>
    public async Task<PickupResult> RunAsync(CancellationToken ct)
    {
        using IDisposable? scope = _log.BeginScope(new Dictionary<string, object>
        {
            ["tenant"] = _bus.Me.Tenant.Value,
            ["machine"] = _bus.Me.Machine.Value,
            ["phase"] = "pickup",
        });
        try
        {
            return await RunCoreAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // spec 13 AC-17: the pickup never throws to the loop; one Error names the type.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogPassFailed(ex.GetType().Name);
            return new PickupResult(0, 0, 0, 0, null);
        }
    }

    /// <summary>
    /// The one inbox line of a report (decision Q-P1, AC-3): <c>- [observed] &lt;created UTC date&gt; [report &lt;job8&gt;]: &lt;first
    /// non-empty body line, trimmed&gt;</c>; <see langword="null"/> when the body has no non-empty line.
    /// </summary>
    internal static string? ReportLine(Model report)
    {
        ArgumentNullException.ThrowIfNull(report);
        string? first = report.BodyText.Split('\n').Select(l => l.Replace("\r", string.Empty, StringComparison.Ordinal)).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
        if (first is null)
        {
            return null;
        }

        string date = report.Header.Created.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return FactLine.Format("observed", string.Empty, "report " + report.Header.InReplyTo!.Value[..8], date, first.Trim());
    }

    private async Task<PickupResult> RunCoreAsync(CancellationToken ct)
    {
        IReadOnlyList<PickupCandidate> candidates;
        try
        {
            candidates = await _bus.ListPickupCandidatesAsync(ct).ConfigureAwait(false);
        }
        catch (BusBusyException ex)
        {
            var busy = new WriteFailed("lock", ex.Message);
            NoteArchiveFailed(busy);
            return new PickupResult(0, 0, 0, 0, busy);
        }

        int inboxed = 0, archivedOnly = 0, dropped = 0, left = 0;
        var archive = new List<(BusFile File, DateTimeOffset Created, MachineName Folder)>();
        bool inboxFailed = false;
        foreach (PickupCandidate candidate in candidates)
        {
            EnvelopeResult verified = await _signer.ParseAndVerifyAsync(candidate.Bytes, _bus.Me.Tenant, ct).ConfigureAwait(false);
            if (!verified.IsAccepted)
            {
                Leave(candidate, PickupLeftWire.FromRejection(verified.Rejection!.Reason), verified.Rejection.Field);
                left++;
                continue;
            }

            Model envelope = verified.Envelope!;
            (PickupLeftReason Reason, string Field)? writer = WriterRule(candidate, envelope);
            if (writer is { } broken)
            {
                Leave(candidate, broken.Reason, broken.Field);
                left++;
                continue;
            }

            if (envelope.Header.To != MachineName.Central)
            {
                continue;
            }

            List<string> lines = Lines(envelope, ref dropped);
            if (lines.Count == 0)
            {
                archivedOnly++;
                archive.Add((candidate.File, envelope.Header.Created, candidate.Folder));
                continue;
            }

            InboxWrite write = _inbox.Write(envelope, lines);
            switch (write.Outcome)
            {
                case InboxWriteOutcome.Created:
                    inboxed++;
                    archive.Add((candidate.File, envelope.Header.Created, candidate.Folder));
                    break;
                case InboxWriteOutcome.AlreadyPresent:
                    archivedOnly++;
                    archive.Add((candidate.File, envelope.Header.Created, candidate.Folder));
                    break;
                default:
                    inboxFailed = true;
                    left++;
                    if (!_inboxFailureLogged)
                    {
                        _inboxFailureLogged = true;
                        LogInboxFailed(envelope.Header.Id.Value, write.Detail ?? "unknown");
                    }

                    break;
            }
        }

        if (!inboxFailed)
        {
            _inboxFailureLogged = false;
        }

        if (archive.Count == 0)
        {
            return new PickupResult(inboxed, archivedOnly, dropped, left, null);
        }

        WriteOutcome outcome = await _bus.ArchiveAsync([.. archive.Select(a => (a.File, a.Created))], ct).ConfigureAwait(false);
        if (outcome is WriteFailed failed)
        {
            NoteArchiveFailed(failed);
        }
        else
        {
            _archiveFailureLogged = false;
            if (_log.IsEnabled(LogLevel.Information))
            {
                string oldest = archive.Min(a => a.Created).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
                int machines = archive.Select(a => a.Folder).Distinct().Count();
                LogPickedUp(archive.Count, machines, inboxed, oldest, outcome is Pushed ? "pushed" : "deferred");
            }
        }

        return new PickupResult(inboxed, archivedOnly, dropped, left, outcome);
    }

    /// <summary>The lines to copy: a context's body minus secret-shaped lines; a report's one line; nothing for a rejected report.</summary>
    private List<string> Lines(Model envelope, ref int dropped)
    {
        string id = envelope.Header.Id.Value;
        if (envelope.Type == EnvelopeType.Report)
        {
            if (envelope.Report!.Status == ReportStatus.Rejected || ReportLine(envelope) is not { } line)
            {
                return [];
            }

            if (_patterns.TryMatch(line, out string refused))
            {
                dropped++;
                LogReportLineRefused(id, refused);
                return [];
            }

            return [line];
        }

        var lines = new List<string>(envelope.BodyText.Split('\n'));
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var kept = new List<string>(lines.Count);
        foreach (string line in lines)
        {
            if (_patterns.TryMatch(line, out string name))
            {
                dropped++;
                LogDroppedLine(id, name);
                continue;
            }

            kept.Add(line);
        }

        return kept.Exists(l => !string.IsNullOrWhiteSpace(l)) ? kept : [];
    }

    /// <summary>The writer rules (§4, the audit's order): file name = <c>id</c>, <c>type</c> = folder, <c>from</c> = folder machine.</summary>
    private static (PickupLeftReason Reason, string Field)? WriterRule(PickupCandidate candidate, Model envelope)
    {
        if (!string.Equals(Path.GetFileNameWithoutExtension(candidate.File.RelativePath), envelope.Header.Id.Value, StringComparison.Ordinal))
        {
            return (PickupLeftReason.IdDiffers, "id");
        }

        EnvelopeType expected = candidate.Kind == BusFolder.Context ? EnvelopeType.Context : EnvelopeType.Report;
        if (envelope.Type != expected)
        {
            return (PickupLeftReason.TypeFolderMismatch, "type");
        }

        return envelope.Header.From != candidate.Folder ? (PickupLeftReason.WriterMismatch, "from") : null;
    }

    private void Leave(PickupCandidate candidate, PickupLeftReason reason, string? field)
    {
        if (_warnedLeft.Add(candidate.File.RelativePath))
        {
            LogLeft(candidate.File.RelativePath, PickupLeftWire.ToWire(reason), field ?? "-");
        }
    }

    private void NoteArchiveFailed(WriteFailed failed)
    {
        if (!_archiveFailureLogged)
        {
            _archiveFailureLogged = true;
            LogArchiveFailed(failed.Stage, failed.Detail);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "picked up {Count} file(s) from {Machines} machine(s): {Inboxed} to inbox, oldest created {Oldest}; archive {Archive}")]
    private partial void LogPickedUp(int count, int machines, int inboxed, string oldest, string archive);

    [LoggerMessage(Level = LogLevel.Warning, Message = "pickup: left {Path}: {Reason} ({Field})")]
    private partial void LogLeft(string path, string reason, string field);

    [LoggerMessage(Level = LogLevel.Warning, Message = "pickup: {Id}: dropped 1 line (pattern {Name})")]
    private partial void LogDroppedLine(string id, string name);

    [LoggerMessage(Level = LogLevel.Warning, Message = "pickup: {Id}: report line refused (pattern {Name})")]
    private partial void LogReportLineRefused(string id, string name);

    [LoggerMessage(Level = LogLevel.Error, Message = "pickup: archive failed at {Stage}: {Detail}")]
    private partial void LogArchiveFailed(string stage, string detail);

    [LoggerMessage(Level = LogLevel.Error, Message = "pickup: inbox write failed for {Id}: {ExceptionType}")]
    private partial void LogInboxFailed(string id, string exceptionType);

    [LoggerMessage(Level = LogLevel.Error, Message = "pickup: pass failed: {ExceptionType}")]
    private partial void LogPassFailed(string exceptionType);
}
