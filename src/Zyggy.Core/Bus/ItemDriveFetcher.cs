using System.Globalization;
using System.Security.Cryptography;

using Microsoft.Extensions.Logging;

using Zyggy.Core.Envelope;
using Zyggy.Core.M365.Graph;
using Zyggy.Core.Memory;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Bus;

/// <summary>What one fetch of a <c>via: onedrive</c> item gave (spec 41 AC-29..AC-37).</summary>
internal abstract record DriveFetch;

/// <summary>The bytes, their size and SHA-256 equal to the signed values, read from <paramref name="DrivePath"/>.</summary>
internal sealed record DriveFetched(byte[] Bytes, string DrivePath) : DriveFetch;

/// <summary>No lookup now: the item's next attempt is not due, or every fetch is held after a 429 (plan 41 A12).</summary>
internal sealed record DriveNotDue : DriveFetch;

/// <summary>The item waits: its file is not on the drive yet (<c>missing</c>) or Graph failed transiently (the cause's wire word).</summary>
internal sealed record DriveWaiting(string Cause) : DriveFetch;

/// <summary>The item is refused with <paramref name="Word"/> (<c>drive_missing</c>, <c>drive_mismatch</c>, <c>too_large</c>).</summary>
internal sealed record DriveRefused(string Word, string Detail, string DrivePath) : DriveFetch;

/// <summary>Central's node cannot read OneDrive: no M365 configuration or credential, or the sign-in failed (AC-37).</summary>
internal sealed record DriveNotConfigured(string Detail) : DriveFetch;

/// <summary>
/// Central's read-only OneDrive fetch for a <c>via: onedrive</c> item (spec 41; founding spec §8 Graph-certificate row as amended by
/// W41-12): the file at <c>&lt;offload.root_folder&gt;/&lt;project&gt;/&lt;drive_name&gt;</c> looked up at most once a minute per item, its
/// metadata size checked before the download, the download capped at the signed size and its SHA-256 checked after. A 429 holds every
/// fetch until <c>Retry-After</c>; a transient failure never refuses; only a 404 after <c>item_drive_wait_hours</c> since <c>created</c>
/// does (<c>drive_missing</c>). The wait state is in memory only (a restart retries at once). GET only — the drive writer is never here.
/// </summary>
internal sealed partial class ItemDriveFetcher(
    Func<(IGraphReader? Reader, string? Error)> reader,
    ArchiveOptions options,
    TimeProvider clock,
    ILogger log)
{
    /// <summary>The least time between two lookups of one item, and the hold after a 429 without <c>Retry-After</c>.</summary>
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    private readonly Dictionary<EnvelopeId, DateTimeOffset> _nextAttempt = [];
    private readonly Dictionary<EnvelopeId, string> _lastCause = [];
    private DateTimeOffset? _holdUntil;
    private bool _signedIn;
    private string? _driveId;

    /// <summary>Fetches <paramref name="item"/>'s bytes, or says why not. Never throws but on cancellation.</summary>
    public async Task<DriveFetch> FetchAsync(Model item, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        ItemFields fields = item.Item is { Via: ItemRoute.OneDrive, DriveName: not null } onedrive ? onedrive : throw new InvalidOperationException("not a OneDrive item");
        EnvelopeId id = item.Header.Id;
        DateTimeOffset now = clock.GetUtcNow();
        if ((_holdUntil is { } hold && now < hold) || (_nextAttempt.TryGetValue(id, out DateTimeOffset due) && now < due))
        {
            return new DriveNotDue();
        }

        (IGraphReader? graph, string? error) = reader();
        if (graph is null)
        {
            return new DriveNotConfigured(error ?? "no Graph reader");
        }

        string extension = Path.GetExtension(fields.DriveName!)[1..];
        string drivePath = new DriveArchivePath(options.Offload.RootFolder).Item(fields.Project, fields.Slug, extension);
        _nextAttempt[id] = now + Interval;
        if (!_signedIn)
        {
            if (await graph.SignInAsync(ct).ConfigureAwait(false) is { } signIn)
            {
                return new DriveNotConfigured(signIn.Message);
            }

            _signedIn = true;
        }

        if (_driveId is null)
        {
            GraphRead<string> drive = await graph.OneDriveIdAsync(ct).ConfigureAwait(false);
            if (drive.Failure is { } driveFailure)
            {
                return Failed(item, driveFailure, now);
            }

            _driveId = drive.Value!;
        }

        GraphRead<DriveItemInfo?> lookup = await graph.ItemByPathAsync(_driveId, drivePath, ct).ConfigureAwait(false);
        if (lookup.Failure is { } lookupFailure)
        {
            return Failed(item, lookupFailure, now);
        }

        if (lookup.Value is not { } info)
        {
            return Missing(item, drivePath, now);
        }

        if (info.IsFolder || info.Size != fields.Size)
        {
            return Settled(id, new DriveRefused("drive_mismatch", "size", drivePath));
        }

        if (info.Size > options.ItemMaxBytes)
        {
            return Settled(id, new DriveRefused("too_large", string.Create(CultureInfo.InvariantCulture, $"{info.Size} > {options.ItemMaxBytes}"), drivePath));
        }

        GraphRead<byte[]?> download = await graph.DownloadAsync(_driveId, drivePath, fields.Size, ct).ConfigureAwait(false);
        if (download.Failure is { } downloadFailure)
        {
            return Failed(item, downloadFailure, now);
        }

        if (download.Value is not { } bytes)
        {
            return Missing(item, drivePath, now);
        }

        return bytes.LongLength != fields.Size || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), fields.Sha256, StringComparison.Ordinal)
            ? Settled(id, new DriveRefused("drive_mismatch", "sha256", drivePath))
            : Settled(id, new DriveFetched(bytes, drivePath));
    }

    private static string Cause(GraphFailureKind kind) => kind switch
    {
        GraphFailureKind.Forbidden => "forbidden",
        GraphFailureKind.Throttled => "throttled",
        GraphFailureKind.Transport => "transport_error",
        GraphFailureKind.Auth => "auth_failed",
        _ => "server_error",
    };

    // A 404: the item waits until item_drive_wait_hours have passed since its signed created, then it is refused (AC-31, AC-33).
    private DriveFetch Missing(Model item, string drivePath, DateTimeOffset now)
    {
        if (now >= item.Header.Created + TimeSpan.FromHours(options.ItemDriveWaitHours))
        {
            return Settled(item.Header.Id, new DriveRefused(
                "drive_missing", string.Create(CultureInfo.InvariantCulture, $"not on OneDrive after {options.ItemDriveWaitHours} h"), drivePath));
        }

        if (Changed(item.Header.Id, "missing"))
        {
            LogWaitsForOneDrive(item.Header.Id.Value);
        }

        return new DriveWaiting("missing");
    }

    // A transient failure: the item waits (never refused, whatever the clock says — A12); a 429 holds every fetch.
    private DriveWaiting Failed(Model item, GraphFailure failure, DateTimeOffset now)
    {
        GraphFailureKind kind = GraphFailureClass.Classify(failure);
        string cause = Cause(kind);
        bool changed = Changed(item.Header.Id, cause);
        if (kind == GraphFailureKind.Throttled)
        {
            TimeSpan wait = failure.RetryAfter is { } retryAfter && retryAfter > TimeSpan.Zero ? retryAfter : Interval;
            bool holding = _holdUntil is { } previous && previous > now;
            _holdUntil = now + wait;
            if (!holding)
            {
                LogThrottled(_holdUntil.Value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            }
        }
        else if (changed && kind == GraphFailureKind.Forbidden)
        {
            LogFetchForbidden(item.Header.Id.Value, cause, failure.Message);
        }
        else if (changed)
        {
            LogFetchFailing(item.Header.Id.Value, cause, failure.Message);
        }

        return new DriveWaiting(cause);
    }

    private DriveFetch Settled(EnvelopeId id, DriveFetch fetch)
    {
        _nextAttempt.Remove(id);
        _lastCause.Remove(id);
        return fetch;
    }

    private bool Changed(EnvelopeId id, string cause)
    {
        if (_lastCause.TryGetValue(id, out string? last) && last == cause)
        {
            return false;
        }

        _lastCause[id] = cause;
        return true;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "pickup: item {Id} waits for OneDrive")]
    private partial void LogWaitsForOneDrive(string id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "pickup: OneDrive fetch throttled: every OneDrive fetch held until {Until}")]
    private partial void LogThrottled(string until);

    [LoggerMessage(Level = LogLevel.Warning, Message = "pickup: OneDrive fetch failing for item {Id}: {Cause} ({Detail})")]
    private partial void LogFetchFailing(string id, string cause, string detail);

    [LoggerMessage(Level = LogLevel.Error, Message = "pickup: OneDrive fetch failing for item {Id}: {Cause} ({Detail}) — runbook \"Pickup: OneDrive fetch failing\"")]
    private partial void LogFetchForbidden(string id, string cause, string detail);
}
