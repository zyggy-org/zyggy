using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Zyggy.Core.Configuration;
using Zyggy.Core.Health;
using Zyggy.Core.Policy;

namespace Zyggy.Core.Registry;

/// <summary>
/// The node's discovery cycle (spec 14, plan A3): scan the <c>devRoots</c>, replace the <see cref="RegistrySnapshot"/>, publish. One cycle at
/// start, then one every <see cref="DiscoveryOptions.Interval"/> on the injected clock; a tick while a cycle runs is skipped, never overlapped.
/// A failing scan keeps the previous snapshot and is warned once per streak; <see cref="FirstScan"/> completes after the first cycle's scan
/// whatever its outcome, so the host never waits forever. Notices are logged once per change of their subject set, never file content.
/// The Node's timer only schedules <see cref="RunAsync"/>.
/// </summary>
public sealed partial class DiscoveryLoop
{
    private readonly DiscoveryScanner _scanner;
    private readonly RegistrySnapshot _snapshot;
    private readonly RegistryPublisher _publisher;
    private readonly IPolicySource _policy;
    private readonly NodeConfiguration _configuration;
    private readonly DiscoveryOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<DiscoveryLoop> _log;
    private readonly TaskCompletionSource _firstScan = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<ScanNoticeKind, HashSet<string>> _noticed = [];
    private int _running;
    private bool _published;
    private bool _scanFailedWarned;

    internal DiscoveryLoop(
        DiscoveryScanner scanner,
        RegistrySnapshot snapshot,
        RegistryPublisher publisher,
        IPolicySource policy,
        NodeConfiguration configuration,
        IOptions<DiscoveryOptions> options,
        TimeProvider clock,
        ILogger<DiscoveryLoop> log)
    {
        _scanner = scanner;
        _snapshot = snapshot;
        _publisher = publisher;
        _policy = policy;
        _configuration = configuration;
        _options = options.Value;
        _clock = clock;
        _log = log;
    }

    /// <summary>Gets a task that completes after the first cycle's scan (successful or not); the host awaits it before admitting any job (A1).</summary>
    public Task FirstScan => _firstScan.Task;

    /// <summary>Gets or sets the source of the <c>health</c> key at publish time; <see langword="null"/> keeps the bus copy's value.</summary>
    public Func<HealthLevel?> Health { get; set; } = () => null;

    /// <summary>Gets or sets what the first publish waits for (the host's readiness, A2); completed by default.</summary>
    public Func<CancellationToken, Task> BeforeFirstPublish { get; set; } = _ => Task.CompletedTask;

    /// <summary>Runs one cycle at start and then one per interval until cancelled.</summary>
    /// <param name="ct">Stops the loop.</param>
    /// <returns>A task that completes when the loop stops.</returns>
    public async Task RunAsync(CancellationToken ct)
    {
        // The file walk is synchronous: each cycle runs on the thread pool so a slow scan never blocks the timer or the host start-up.
        Task current = Task.Run(() => RunCycleAsync(ct), ct);
        using var timer = new PeriodicTimer(_options.Interval, _clock);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (!current.IsCompleted)
                {
                    LogSkipped(_log);
                    continue;
                }

                current = Task.Run(() => RunCycleAsync(ct), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // stopping
        }

        try
        {
            await current.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // stopping
        }
    }

    /// <summary>Runs one scan → snapshot → publish cycle; a concurrent call returns at once.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the cycle ends.</returns>
    public async Task RunCycleAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            LogSkipped(_log);
            return;
        }

        try
        {
            using IDisposable? scope = _log.BeginScope(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["tenant"] = _configuration.Tenant.Value,
                ["machine"] = _configuration.Machine.Value,
                ["phase"] = "discover",
            });
            ScanResult? scan = await ScanAsync(ct).ConfigureAwait(false);
            if (scan is null)
            {
                _firstScan.TrySetResult();
                return;
            }

            LogNotices(scan.Notices);
            RegistryDocument document = RegistryDocument.FromScan(_configuration, scan, _clock.GetUtcNow(), health: null);
            _snapshot.Replace(document);
            _firstScan.TrySetResult();
            if (!_published)
            {
                await BeforeFirstPublish(ct).ConfigureAwait(false);
                _published = true;
            }

            await _publisher.PublishAsync(document, Health(), ct).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private async Task<ScanResult?> ScanAsync(CancellationToken ct)
    {
        try
        {
            NodePolicy policy = await _policy.GetAsync(ct).ConfigureAwait(false);
            ScanResult scan = await _scanner.ScanAsync(policy.DevRoots, _options.Detail, ct).ConfigureAwait(false);
            _scanFailedWarned = false;
            return scan;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (!_scanFailedWarned)
            {
                _scanFailedWarned = true;
                LogScanFailed(_log, exception.GetType().Name);
            }

            return null;
        }
    }

    private void LogNotices(IReadOnlyList<ScanNotice> notices)
    {
        foreach (ScanNoticeKind kind in Enum.GetValues<ScanNoticeKind>())
        {
            List<ScanNotice> current = notices.Where(n => n.Kind == kind).ToList();
            HashSet<string> previous = _noticed.TryGetValue(kind, out HashSet<string>? seen) ? seen : [];
            var subjects = new HashSet<string>(StringComparer.Ordinal);
            foreach (ScanNotice notice in current)
            {
                string key = notice.Subject + "\n" + notice.Detail;
                subjects.Add(key);
                if (!previous.Contains(key))
                {
                    LogNotice(_log, NoticeText(notice));
                }
            }

            _noticed[kind] = subjects;
        }
    }

    private static string NoticeText(ScanNotice notice) => notice.Kind switch
    {
        ScanNoticeKind.DuplicateName => $"duplicate name {notice.Subject}: {notice.Detail}",
        ScanNoticeKind.RootMissing => $"root missing {notice.Subject}",
        ScanNoticeKind.RootUnreadable => $"root unreadable {notice.Subject}",
        ScanNoticeKind.NameUnusable => $"name unusable for {notice.Subject}",
        ScanNoticeKind.ProjectCapReached => $"project cap reached ({notice.Detail})",
        ScanNoticeKind.EntryCapReached => $"entry cap reached in {notice.Subject} ({notice.Detail})",
        ScanNoticeKind.SecretRedacted => $"secret pattern {notice.Detail} redacted in {notice.Subject}",
        ScanNoticeKind.PatternsUnavailable => "secret patterns not registered; publishing names only",
        ScanNoticeKind.SlowScan => $"scan took {notice.Detail}",
        _ => notice.Kind + " " + notice.Subject,
    };

    [LoggerMessage(Level = LogLevel.Warning, Message = "discovery: {Notice}")]
    private static partial void LogNotice(ILogger logger, string notice);

    [LoggerMessage(Level = LogLevel.Warning, Message = "discovery: scan failed {ExceptionType}")]
    private static partial void LogScanFailed(ILogger logger, string exceptionType);

    [LoggerMessage(Level = LogLevel.Debug, Message = "discovery: scan skipped, previous still running")]
    private static partial void LogSkipped(ILogger logger);
}
