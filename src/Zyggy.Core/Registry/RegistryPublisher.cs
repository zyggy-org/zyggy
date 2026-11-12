using System.Globalization;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Zyggy.Core.Bus;
using Zyggy.Core.Health;

namespace Zyggy.Core.Registry;

/// <summary>
/// Publishes this machine's registry file (founding spec §4 "Registry file" as amended by A14-1): reads the bus copy, commits only when
/// the content differs or <c>last_seen</c> is older than <see cref="DiscoveryOptions.LastSeenHours"/> (a future stamp counts as fresh, an
/// unreadable copy as absent), and refreshes <c>last_seen</c> and <c>health</c> on every commit. Logs once per state change, never content.
/// Holds only its own streak flags; one instance per host.
/// </summary>
public sealed partial class RegistryPublisher
{
    private readonly BusRepository _bus;
    private readonly DiscoveryOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<RegistryPublisher> _log;
    private readonly Lock _gate = new();

    private bool _unreadableWarned;
    private bool _retryWarned;
    private bool _pushFailedWarned;
    private bool _busyWarned;

    internal RegistryPublisher(BusRepository bus, IOptions<DiscoveryOptions> options, TimeProvider clock, ILogger<RegistryPublisher> log)
    {
        _bus = bus;
        _options = options.Value;
        _clock = clock;
        _log = log;
    }

    /// <summary>Publishes <paramref name="content"/> when needed.</summary>
    /// <param name="content">The scanned document; its <c>last_seen</c> and <c>health</c> are replaced.</param>
    /// <param name="health">The current verdict, or <see langword="null"/> to keep the bus copy's value (omitted when there is none).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The outcome with the redacted failure detail.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="content"/> is null.</exception>
    public async Task<RegistryPublishResult> PublishAsync(RegistryDocument content, HealthLevel? health, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        using IDisposable? scope = _log.BeginScope(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["tenant"] = _bus.Me.Tenant.Value,
            ["machine"] = _bus.Me.Machine.Value,
            ["phase"] = "discover",
        });

        RegistryDocument? copy = await ReadCopyAsync(ct).ConfigureAwait(false);
        DateTimeOffset now = _clock.GetUtcNow();
        bool fresh = copy?.LastSeen is { } seen && (seen > now || now - seen < TimeSpan.FromHours(_options.LastSeenHours));
        if (copy is not null && fresh && copy.ContentEquals(content))
        {
            LogUnchanged(_log);
            return new RegistryPublishResult(PublishOutcome.Unchanged, null);
        }

        var stamp = new DateTimeOffset(now.UtcDateTime.Year, now.UtcDateTime.Month, now.UtcDateTime.Day, now.UtcDateTime.Hour, now.UtcDateTime.Minute, now.UtcDateTime.Second, TimeSpan.Zero);
        RegistryDocument document = content with { LastSeen = stamp, Health = health ?? copy?.Health };
        WriteOutcome outcome = await _bus.PublishRegistryAsync(document, ct).ConfigureAwait(false);
        return Settle(outcome, document.Projects.Count);
    }

    private async Task<RegistryDocument?> ReadCopyAsync(CancellationToken ct)
    {
        byte[]? bytes = await _bus.ReadOwnRegistryAsync(ct).ConfigureAwait(false);
        if (bytes is null)
        {
            return null;
        }

        RegistryParse parse = RegistryDocument.Parse(bytes);
        lock (_gate)
        {
            if (parse.IsOk)
            {
                _unreadableWarned = false;
                return parse.Document;
            }

            if (!_unreadableWarned)
            {
                _unreadableWarned = true;
                LogUnreadable(_log, parse.Field!);
            }
        }

        return null;
    }

    private RegistryPublishResult Settle(WriteOutcome outcome, int projects)
    {
        lock (_gate)
        {
            switch (outcome)
            {
                case Pushed pushed:
                    if (_pushFailedWarned || _busyWarned)
                    {
                        LogPublishingAgain(_log);
                    }

                    _pushFailedWarned = false;
                    _busyWarned = false;
                    if (pushed.Attempts > 1)
                    {
                        if (!_retryWarned)
                        {
                            _retryWarned = true;
                            LogRetried(_log);
                        }
                    }
                    else
                    {
                        _retryWarned = false;
                    }

                    LogPublished(_log, projects);
                    return new RegistryPublishResult(PublishOutcome.Published, null);

                case WriteFailed { Stage: "lock" }:
                    if (!_busyWarned)
                    {
                        _busyWarned = true;
                        LogBusy(_log);
                    }

                    return new RegistryPublishResult(PublishOutcome.Busy, "the bus checkout is busy");

                case Deferred deferred:
                    return Failed(BusRepository.Redacted(deferred.Detail));

                case WriteFailed failed:
                    return Failed(string.Create(CultureInfo.InvariantCulture, $"{failed.Stage}: {BusRepository.Redacted(failed.Detail)}"));

                default:
                    return Failed(outcome.GetType().Name);
            }
        }
    }

    private RegistryPublishResult Failed(string detail)
    {
        if (!_pushFailedWarned)
        {
            _pushFailedWarned = true;
            LogPushFailed(_log, detail);
        }

        return new RegistryPublishResult(PublishOutcome.Failed, detail);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "registry published: {Count} project(s)")]
    private static partial void LogPublished(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "registry unchanged")]
    private static partial void LogUnchanged(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "registry: push retried")]
    private static partial void LogRetried(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "registry: push failed: {Detail}")]
    private static partial void LogPushFailed(ILogger logger, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "registry: checkout busy")]
    private static partial void LogBusy(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "registry: publishing again")]
    private static partial void LogPublishingAgain(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "registry: bus copy unreadable ({Field}); rewriting")]
    private static partial void LogUnreadable(ILogger logger, string field);
}
