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
/// secret-shaped lines, a report as one <c>[observed]</c> line — and is archived in one commit. Spec 41: after them, every item
/// another machine addressed to Central is filed through the one archive writer (memory git only on this path, only when an item
/// is present) and settled in the same commit with Central's signed report. Never calls a model, never throws to the loop except
/// on cancellation.
/// </summary>
public sealed partial class BusPickup
{
    /// <summary>The report reason of an item refused on Central (founding spec §4 as amended by W41-14).</summary>
    internal const string ArchiveRefusedReason = "archive_refused";

    private readonly BusRepository _bus;
    private readonly EnvelopeSigner _signer;
    private readonly PickupInbox _inbox;
    private readonly SecretPatterns _patterns;
    private readonly ILogger<BusPickup> _log;
    private readonly HashSet<string> _warnedLeft = new(StringComparer.Ordinal);
    private readonly HashSet<string> _itemNotes = new(StringComparer.Ordinal);
    private readonly KeyId? _signWith;
    private readonly ItemFiler? _items;
    private readonly string? _archiveError;
    private readonly ItemDriveFetcher? _drive;
    private bool _archiveFailureLogged;
    private bool _inboxFailureLogged;
    private bool _archiveMissingLogged;
    private bool _signFailureLogged;
    private bool _oneDriveLogged;

    internal BusPickup(BusRepository bus, EnvelopeSigner signer, PickupInbox inbox, SecretPatterns patterns, ILogger<BusPickup> log)
        : this(bus, signer, inbox, patterns, log, null, null, null)
    {
    }

    /// <summary>
    /// Spec 41: the pickup that also settles <c>items/</c>. <paramref name="items"/> is <see langword="null"/> when the archive part did
    /// not load (<paramref name="archiveError"/> says why): every item then waits. <paramref name="drive"/> fetches a <c>via: onedrive</c>
    /// item's bytes read-only; without it those items wait.
    /// </summary>
    internal BusPickup(
        BusRepository bus,
        EnvelopeSigner signer,
        PickupInbox inbox,
        SecretPatterns patterns,
        ILogger<BusPickup> log,
        KeyId? signWith,
        ItemFiler? items,
        string? archiveError,
        ItemDriveFetcher? drive = null)
    {
        _bus = bus;
        _signer = signer;
        _inbox = inbox;
        _patterns = patterns;
        _log = log;
        _signWith = signWith;
        _items = items;
        _archiveError = archiveError;
        _drive = drive;
    }

    private enum PickupItemOutcome
    {
        Filed,
        AlreadyFiled,
        Refused,
        Waits,
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

        int inboxed = 0, archivedOnly = 0, dropped = 0, left = 0, filed = 0, refused = 0, waiting = 0;
        var archive = new List<(BusFile File, DateTimeOffset Created, MachineName Folder)>();
        var settled = new List<(SettledItem Item, DateTimeOffset Created, MachineName Folder)>();
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

            if (candidate.Kind == BusFolder.Items)
            {
                // Spec 41 AC-35: an item not addressed to Central is left (a context is skipped).
                if (envelope.Header.To != MachineName.Central)
                {
                    Leave(candidate, PickupLeftReason.WrongRecipient, "to");
                    left++;
                    continue;
                }

                (PickupItemOutcome itemOutcome, SettledItem? item) = await SettleItemAsync(candidate, envelope, ct).ConfigureAwait(false);
                switch (itemOutcome)
                {
                    case PickupItemOutcome.Filed or PickupItemOutcome.AlreadyFiled:
                        filed++;
                        settled.Add((item!, envelope.Header.Created, candidate.Folder));
                        break;
                    case PickupItemOutcome.Refused:
                        refused++;
                        settled.Add((item!, envelope.Header.Created, candidate.Folder));
                        break;
                    default:
                        waiting++;
                        break;
                }

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

        if (archive.Count == 0 && settled.Count == 0)
        {
            return new PickupResult(inboxed, archivedOnly, dropped, left, null) { ItemsFiled = filed, ItemsRefused = refused, ItemsWaiting = waiting };
        }

        // One bus commit per pass (spec 41 Behaviors): moves, then each settled item removed with Central's report added.
        WriteOutcome outcome = await _bus.SettleAsync([.. archive.Select(a => (a.File, a.Created))], [.. settled.Select(s => s.Item)], ct).ConfigureAwait(false);
        if (outcome is WriteFailed failed)
        {
            NoteArchiveFailed(failed);
        }
        else
        {
            _archiveFailureLogged = false;
            if (_log.IsEnabled(LogLevel.Information))
            {
                var all = archive.Select(a => (a.Created, a.Folder)).Concat(settled.Select(s => (s.Created, s.Folder))).ToList();
                string oldest = all.Min(a => a.Created).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
                int machines = all.Select(a => a.Folder).Distinct().Count();
                string pushed = outcome is Pushed ? "pushed" : "deferred";
                if (settled.Count == 0)
                {
                    LogPickedUp(all.Count, machines, inboxed, oldest, pushed);
                }
                else
                {
                    LogPickedUpWithItems(all.Count, machines, inboxed, oldest, pushed, filed, refused);
                }
            }
        }

        return new PickupResult(inboxed, archivedOnly, dropped, left, outcome) { ItemsFiled = filed, ItemsRefused = refused, ItemsWaiting = waiting };
    }

    /// <summary>
    /// One item (spec 41 AC-28, AC-33..AC-41): filed through the one archive writer with a signed <c>done</c> report, refused with a
    /// <c>failed</c> / <c>archive_refused</c> report, or waiting. Any exception but cancellation is caught: one Error naming its type,
    /// the item stays. Never a model, never <c>ZYGGY_HOOKS</c>.
    /// </summary>
    private async Task<(PickupItemOutcome Outcome, SettledItem? Item)> SettleItemAsync(PickupCandidate candidate, Model envelope, CancellationToken ct)
    {
        try
        {
            return await SettleItemCoreAsync(candidate, envelope, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // spec 41 AC-41: an exception in the item path leaves the item; one Error names the type.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            string type = ex.GetType().Name;
            if (_itemNotes.Add(envelope.Header.Id.Value + "|failed|" + type))
            {
                LogItemFailed(envelope.Header.Id.Value, type);
            }

            return (PickupItemOutcome.Waits, null);
        }
    }

    private async Task<(PickupItemOutcome Outcome, SettledItem? Item)> SettleItemCoreAsync(PickupCandidate candidate, Model envelope, CancellationToken ct)
    {
        if (_signWith is null)
        {
            NoteSignFailed("signWith missing: the node must sign its reports");
            return (PickupItemOutcome.Waits, null);
        }

        if (_items is null)
        {
            if (!_archiveMissingLogged)
            {
                _archiveMissingLogged = true;
                LogArchiveMissing(_archiveError ?? "the pickup was built without it");
            }

            return (PickupItemOutcome.Waits, null);
        }

        bool oneDrive = envelope.Item!.Via == ItemRoute.OneDrive;
        if (oneDrive && _drive is null)
        {
            NoteOneDriveNotConfigured("the pickup was built without a OneDrive fetcher");
            return (PickupItemOutcome.Waits, null);
        }

        // The report key first, so nothing is fetched or filed that could not be answered.
        SigningKeyCheck key = await _signer.CheckKeyAsync(_signWith, ct).ConfigureAwait(false);
        if (key.State != SigningKeyState.Usable)
        {
            NoteSignFailed(key.Detail);
            return (PickupItemOutcome.Waits, null);
        }

        if (!oneDrive)
        {
            return await SettleAsync(candidate, envelope, await _items.FileBusAsync(envelope, ct).ConfigureAwait(false), null, ct).ConfigureAwait(false);
        }

        // Spec 41 AC-29: the bytes downloaded read-only, then filed exactly like a bus item.
        DriveFetch fetch = await _drive!.FetchAsync(envelope, ct).ConfigureAwait(false);
        switch (fetch)
        {
            case DriveNotConfigured notConfigured:
                NoteOneDriveNotConfigured(notConfigured.Detail);
                return (PickupItemOutcome.Waits, null);
            case DriveNotDue:
                return (PickupItemOutcome.Waits, null);
            case DriveWaiting:
                _oneDriveLogged = false;
                return (PickupItemOutcome.Waits, null);
        }

        _oneDriveLogged = false;
        return fetch switch
        {
            DriveRefused refused => await RefuseAsync(candidate, envelope, refused.Word, refused.Detail, refused.DrivePath, ct).ConfigureAwait(false),
            DriveFetched fetched when ItemFiler.DriveTypeDiffers(envelope.Item, fetched.Bytes) =>
                await RefuseAsync(candidate, envelope, "drive_mismatch", "type", fetched.DrivePath, ct).ConfigureAwait(false),
            DriveFetched fetched =>
                await SettleAsync(candidate, envelope, await _items.FileAsync(envelope, fetched.Bytes, ct).ConfigureAwait(false), fetched.DrivePath, ct).ConfigureAwait(false),
            _ => throw new InvalidOperationException("unknown drive fetch " + fetch.GetType().Name),
        };
    }

    // The writer's outcome as a settled item, or a wait (memory busy, push rejected before the commit, …).
    private async Task<(PickupItemOutcome Outcome, SettledItem? Item)> SettleAsync(
        PickupCandidate candidate, Model envelope, ArchiveOutcome filed, string? drivePath, CancellationToken ct)
    {
        string id = envelope.Header.Id.Value;
        switch (filed.Kind)
        {
            case ArchiveOutcomeKind.Archived or ArchiveOutcomeKind.AlreadyFiled:
                PickupItemOutcome outcome = filed.Kind == ArchiveOutcomeKind.Archived ? PickupItemOutcome.Filed : PickupItemOutcome.AlreadyFiled;
                (PickupItemOutcome, SettledItem?) done = await ReportAsync(
                    candidate, envelope, outcome, new ReportFields(ReportStatus.Done), PickupItemReport.DoneBody(filed, drivePath), "filed " + filed.Item, ct).ConfigureAwait(false);
                if (done.Item2 is not null && filed.Kind == ArchiveOutcomeKind.Archived && !filed.Pushed && _itemNotes.Add(id + "|deferred"))
                {
                    LogItemPushDeferred(id);
                }

                return done;
            case ArchiveOutcomeKind.Refused or ArchiveOutcomeKind.Usage:
                // A composed index line over 400 characters cannot come from a checked laptop; it is refused like any other check.
                string word = filed.Refusal is { } refusal ? ArchiveRefusalWire.ToWire(refusal) : "line_too_long";
                return await RefuseAsync(candidate, envelope, word, filed.Refusal is null ? null : filed.Detail, drivePath, ct).ConfigureAwait(false);
            default:
                if (_itemNotes.Add(id + "|waits|" + filed.Detail))
                {
                    LogItemWaits(id, filed.Detail ?? "git_error");
                }

                return (PickupItemOutcome.Waits, null);
        }
    }

    // A refusal (a writer check, or drive_missing / drive_mismatch / too_large before the writer): failed / archive_refused, one Warning per id.
    private Task<(PickupItemOutcome Outcome, SettledItem? Item)> RefuseAsync(
        PickupCandidate candidate, Model envelope, string word, string? detail, string? drivePath, CancellationToken ct)
    {
        string id = envelope.Header.Id.Value;
        if (_itemNotes.Add(id + "|refused"))
        {
            LogItemRefused(id, word, detail ?? "-");
        }

        return ReportAsync(
            candidate,
            envelope,
            PickupItemOutcome.Refused,
            new ReportFields(ReportStatus.Failed, ArchiveRefusedReason),
            PickupItemReport.RefusedBody(word, detail, drivePath),
            "refused " + word,
            ct);
    }

    // Central's signed report to the sender, in reply to the item; a signing failure makes the item wait.
    private async Task<(PickupItemOutcome Outcome, SettledItem? Item)> ReportAsync(
        PickupCandidate candidate, Model envelope, PickupItemOutcome outcome, ReportFields fields, byte[] body, string line, CancellationToken ct)
    {
        DateTimeOffset now = _bus.Clock.GetUtcNow();
        var header = new EnvelopeHeader(_bus.Me.Tenant, EnvelopeId.New(now), MachineName.Central, envelope.Header.From, now, EnvelopePriority.Normal, envelope.Header.Id);
        EnvelopeResult signed = await _signer.SignAsync(Model.CreateReport(header, fields, body), _signWith!, ct).ConfigureAwait(false);
        if (!signed.IsAccepted)
        {
            NoteSignFailed(signed.Rejection!.Detail);
            return (PickupItemOutcome.Waits, null);
        }

        _signFailureLogged = false;
        return (outcome, new SettledItem(candidate.File, signed.Envelope!, line));
    }

    private void NoteOneDriveNotConfigured(string detail)
    {
        if (!_oneDriveLogged)
        {
            _oneDriveLogged = true;
            LogOneDriveNotConfigured(detail);
        }
    }

    private void NoteSignFailed(string detail)
    {
        if (!_signFailureLogged)
        {
            _signFailureLogged = true;
            LogCannotSign(detail);
        }
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

        EnvelopeType expected = candidate.Kind switch
        {
            BusFolder.Context => EnvelopeType.Context,
            BusFolder.Items => EnvelopeType.Item,
            _ => EnvelopeType.Report,
        };
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

    [LoggerMessage(Level = LogLevel.Information, Message = "picked up {Count} file(s) from {Machines} machine(s): {Inboxed} to inbox, oldest created {Oldest}; archive {Archive}; items filed {Filed}, refused {Refused}")]
    private partial void LogPickedUpWithItems(int count, int machines, int inboxed, string oldest, string archive, int filed, int refused);

    [LoggerMessage(Level = LogLevel.Warning, Message = "pickup: item {Id} refused: {Word} ({Detail})")]
    private partial void LogItemRefused(string id, string word, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "pickup: item {Id} waits: memory {Detail}")]
    private partial void LogItemWaits(string id, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "pickup: item {Id} filed; memory push deferred")]
    private partial void LogItemPushDeferred(string id);

    [LoggerMessage(Level = LogLevel.Error, Message = "pickup: item {Id} failed: {ExceptionType}")]
    private partial void LogItemFailed(string id, string exceptionType);

    [LoggerMessage(Level = LogLevel.Error, Message = "pickup: archive configuration missing: {Detail}")]
    private partial void LogArchiveMissing(string detail);

    [LoggerMessage(Level = LogLevel.Error, Message = "pickup: cannot sign reports: {Detail}")]
    private partial void LogCannotSign(string detail);

    [LoggerMessage(Level = LogLevel.Error, Message = "pickup: OneDrive fetch not configured: {Detail} (OneDrive-route items wait)")]
    private partial void LogOneDriveNotConfigured(string detail);

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
