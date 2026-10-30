using Microsoft.Extensions.Logging;

using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Bus;

/// <summary>
/// The read side of <c>zyggy status</c> (spec 08 AC-5): the tenant's machines with their folder counts, the newest reports, and one
/// job traced through <c>jobs/</c>, <c>claimed/</c>, <c>rejected/</c>, every <c>reports/</c> folder and the archive. Reads under the
/// checkout's write lock (so a concurrent claim or report never shows a half-moved file), never the listen lock, and writes nothing.
/// Only this tenant's subtree is ever opened.
/// </summary>
public sealed partial class BusInventory
{
    private const string EnvelopeFilter = "*.md";

    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(60);

    private readonly BusRepository _bus;
    private readonly string _root;
    private readonly EnvelopeSigner _signer;
    private readonly TimeProvider _clock;
    private readonly ILogger<BusInventory> _log;
    private readonly BusIdentity _me;

    /// <summary>Creates the inventory over the checkout at <paramref name="checkoutRoot"/>.</summary>
    /// <param name="bus">The bus repository (its identity names the tenant; its <c>HEAD</c> is reported).</param>
    /// <param name="checkoutRoot">The checkout root.</param>
    /// <param name="signer">The envelope signer, for verification.</param>
    /// <param name="clock">The clock (the lock's deadline).</param>
    /// <param name="log">The logger.</param>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    public BusInventory(BusRepository bus, string checkoutRoot, EnvelopeSigner signer, TimeProvider clock, ILogger<BusInventory> log)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        ArgumentException.ThrowIfNullOrEmpty(checkoutRoot);
        _root = checkoutRoot;
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _me = bus.Me;
    }

    /// <summary>Every machine's counts and the newest <paramref name="recent"/> reports across all machines.</summary>
    /// <param name="recent">How many reports to summarise (0 for none).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The report.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="recent"/> is negative.</exception>
    /// <exception cref="BusBusyException">Another zyggy process held the checkout's write lock for the whole wait.</exception>
    public async Task<InventoryReport> ListAsync(int recent, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(recent);
        using IDisposable handle = await LockAsync(ct).ConfigureAwait(false);
        IReadOnlyList<MachineName> machines = Machines();
        var counts = new List<MachineCounts>(machines.Count);
        var reportFiles = new List<(string Path, EnvelopeId Id)>();
        foreach (MachineName machine in machines)
        {
            counts.Add(new MachineCounts(
                machine,
                Count(BusPaths.Jobs(_me.Tenant, machine)),
                Count(BusPaths.Claimed(_me.Tenant, machine)),
                Count(BusPaths.Rejected(_me.Tenant, machine)),
                Count(BusPaths.Reports(_me.Tenant, machine)),
                Count(BusPaths.Context(_me.Tenant, machine))));
            reportFiles.AddRange(Envelopes(BusPaths.Reports(_me.Tenant, machine)));
        }

        // spec 13 AC-25: Central archives reports within seconds, so the archive months are part of the report list
        foreach (string month in ArchiveMonths.Folders(_root, _me.Tenant))
        {
            reportFiles.AddRange(Envelopes(month).Where(f => EnvelopeParser.PeekHeader(File.ReadAllBytes(BusPaths.Resolve(_root, f.Path)))?.Type == EnvelopeType.Report));
        }

        var summaries = new List<EnvelopeSummary>();
        foreach ((string path, EnvelopeId id) in reportFiles.OrderByDescending(f => f.Id.Value, StringComparer.Ordinal).Take(recent))
        {
            summaries.Add((await SummariseAsync(path, id, ct).ConfigureAwait(false)).Summary);
        }

        string? head = await _bus.HeadShaAsync(ct).ConfigureAwait(false);
        return new InventoryReport(head, counts, summaries, TenantDirectories()) { ReportsTotal = reportFiles.Count };
    }

    /// <summary>Where <paramref name="id"/> stands, with its job file (if any) and every report answering it.</summary>
    /// <param name="id">The job id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The trace, or <see langword="null"/> when nothing on the bus names the id.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="id"/> is null.</exception>
    /// <exception cref="BusBusyException">Another zyggy process held the checkout's write lock for the whole wait.</exception>
    public async Task<EnvelopeTrace?> TraceAsync(EnvelopeId id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(id);
        using IDisposable handle = await LockAsync(ct).ConfigureAwait(false);
        (string Path, MachineName Machine, TraceState State)? hit = null;
        foreach (MachineName machine in Machines())
        {
            foreach ((string folder, TraceState folderState) in new[]
                     {
                         (BusPaths.Jobs(_me.Tenant, machine), TraceState.Queued),
                         (BusPaths.Claimed(_me.Tenant, machine), TraceState.Claimed),
                         (BusPaths.Rejected(_me.Tenant, machine), TraceState.Rejected),
                     })
            {
                string candidate = BusPaths.EnvelopeFile(folder, id);
                if (!File.Exists(BusPaths.Resolve(_root, candidate)))
                {
                    continue;
                }

                if (hit is null)
                {
                    hit = (candidate, machine, folderState);
                }
                else
                {
                    LogFoundTwice(id.Value, hit.Value.Path, candidate);
                }
            }
        }

        var reports = new List<EnvelopeSummary>();
        string? body = null;
        foreach ((string path, EnvelopeId reportId) in ReportsAnswering(id))
        {
            (EnvelopeSummary summary, Envelope.Envelope? verified) = await SummariseAsync(path, reportId, ct).ConfigureAwait(false);
            reports.Add(summary);
            if (verified is not null)
            {
                body = verified.BodyText;
            }
        }

        EnvelopeSummary? job = hit is null ? null : (await SummariseAsync(hit.Value.Path, id, ct).ConfigureAwait(false)).Summary;
        bool reported = reports.Any(r => r.Verified && r.Status != ReportStatus.Rejected);
        bool rejectedByReport = reports.Any(r => r.Verified && r.Status == ReportStatus.Rejected);
        TraceState? state = reported ? TraceState.Reported
            : hit?.State == TraceState.Rejected || rejectedByReport ? TraceState.Rejected
            : hit?.State;
        if (state is null)
        {
            return null;
        }

        MachineName? where = hit?.Machine ?? reports.LastOrDefault(r => r.Verified)?.From ?? reports.LastOrDefault()?.From;
        return new EnvelopeTrace(id, state.Value, where, job, reports, body);
    }

    private async Task<IDisposable> LockAsync(CancellationToken ct) =>
        await BusCheckoutLock.AcquireAsync(_root, BusCheckoutLock.WriteLockName, LockTimeout, _clock, ct).ConfigureAwait(false)
        ?? throw new BusBusyException();

    /// <summary>The machine directories under <c>nodes/</c> whose names are labels, ordinal order; anything else is the auditor's business.</summary>
    private IReadOnlyList<MachineName> Machines()
    {
        string nodes = BusPaths.Resolve(_root, BusPaths.Nodes(_me.Tenant));
        if (!Directory.Exists(nodes))
        {
            return [];
        }

        var machines = new List<MachineName>();
        foreach (string directory in Directory.EnumerateDirectories(nodes))
        {
            if (MachineName.TryParse(Path.GetFileName(directory), out MachineName? machine))
            {
                machines.Add(machine);
            }
        }

        return [.. machines.OrderBy(m => m.Value, StringComparer.Ordinal)];
    }

    /// <summary>The directory names under <c>tenants/</c>, ordinal order — names only, nothing inside another tenant is opened.</summary>
    private IReadOnlyList<string> TenantDirectories()
    {
        string tenants = Path.Join(_root, BusPaths.Tenants);
        return Directory.Exists(tenants)
            ? [.. Directory.EnumerateDirectories(tenants).Select(Path.GetFileName).Where(n => n is not null).Select(n => n!).Order(StringComparer.Ordinal)]
            : [];
    }

    private int Count(string folder) => Envelopes(folder).Count();

    /// <summary>The <c>*.md</c> files of one folder (top level) whose stem is an id.</summary>
    private IEnumerable<(string Path, EnvelopeId Id)> Envelopes(string folder)
    {
        string full = BusPaths.Resolve(_root, folder);
        if (!Directory.Exists(full))
        {
            yield break;
        }

        foreach (string file in Directory.EnumerateFiles(full, EnvelopeFilter, SearchOption.TopDirectoryOnly))
        {
            if (EnvelopeId.TryParse(Path.GetFileNameWithoutExtension(file), out EnvelopeId? id))
            {
                yield return (folder + "/" + Path.GetFileName(file), id);
            }
        }
    }

    /// <summary>Every report under every machine's <c>reports/</c> and every month of the archive whose <c>in_reply_to</c> is <paramref name="id"/>, oldest first.</summary>
    private IEnumerable<(string Path, EnvelopeId Id)> ReportsAnswering(EnvelopeId id)
    {
        var candidates = new List<(string Path, EnvelopeId Id)>();
        foreach (MachineName machine in Machines())
        {
            candidates.AddRange(Envelopes(BusPaths.Reports(_me.Tenant, machine)));
        }

        foreach (string month in ArchiveMonths.Folders(_root, _me.Tenant))
        {
            candidates.AddRange(Envelopes(month));
        }

        foreach ((string path, EnvelopeId reportId) in candidates.OrderBy(c => c.Id.Value, StringComparer.Ordinal))
        {
            PeekedHeader? peeked = EnvelopeParser.PeekHeader(File.ReadAllBytes(BusPaths.Resolve(_root, path)));
            if (peeked?.InReplyTo == id)
            {
                yield return (path, reportId);
            }
        }
    }

    /// <summary>Verified: the typed fields and the envelope; otherwise the header read leniently, the §4 reason named, and no envelope.</summary>
    private async Task<(EnvelopeSummary Summary, Envelope.Envelope? Verified)> SummariseAsync(string path, EnvelopeId id, CancellationToken ct)
    {
        byte[] bytes = await File.ReadAllBytesAsync(BusPaths.Resolve(_root, path), ct).ConfigureAwait(false);
        EnvelopeResult result = await _signer.ParseAndVerifyAsync(bytes, _me.Tenant, ct).ConfigureAwait(false);
        if (result.IsAccepted)
        {
            Envelope.Envelope e = result.Envelope!;
            return (new EnvelopeSummary(path, id, e.Type, e.Header.From, e.Header.To, e.Header.Created, e.Header.InReplyTo, e.Report?.Status, e.Report?.Reason, e.Report?.CostUsd, true, null, e.Job?.Project, e.Header.Priority), e);
        }

        PeekedHeader? peeked = EnvelopeParser.PeekHeader(bytes);
        return (new EnvelopeSummary(path, id, peeked?.Type, peeked?.From, peeked?.To, peeked?.Created, peeked?.InReplyTo, peeked?.Status, peeked?.Reason, peeked?.CostUsd, false, EnvelopeWire.ToWire(result.Rejection!.Reason)), null);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "job {Id} found twice: {First}, {Second}")]
    private partial void LogFoundTwice(string id, string first, string second);
}
