using Microsoft.Extensions.Logging;

using Zyggy.Core.Configuration;
using Zyggy.Core.Envelope;
using Zyggy.Core.Memory;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Bus;

/// <summary>
/// The sender side of an item envelope (founding spec §4 as amended by deliverable 41): runs every archive check a laptop can run on
/// the file before anything leaves it, checks the signing key and pulls (plan 41 A7), builds the item addressed to Central from this
/// node, signs it, proves by a round trip — serialise, re-parse, re-verify, compare every §4 item field, the body and the key set — that
/// the bytes read back as the request, and publishes it into this node's own <c>items/</c> folder. <see cref="ContextSubmitter"/>'s shape.
/// </summary>
public sealed partial class ItemSubmitter
{
    private static readonly HashSet<string> Section4ItemKeys =
    [
        "schema", "id", "tenant", "type", "from", "to", "created", "in_reply_to", "priority",
        "via", "project", "slug", "drive_name", "name", "description", "source_name", "size", "sha256", "key_id", "sig",
    ];

    private const string StoreUnavailablePrefix = "secret store unavailable: ";

    private readonly BusRepository _bus;
    private readonly EnvelopeSigner _signer;
    private readonly KeyId _signWith;
    private readonly SecretPatterns _patterns;
    private readonly NodeConfiguration _node;
    private readonly SourceDenyList _deny;
    private readonly TimeProvider _clock;
    private readonly ILogger<ItemSubmitter> _log;

    internal ItemSubmitter(
        BusRepository bus,
        EnvelopeSigner signer,
        KeyId signWith,
        SecretPatterns patterns,
        NodeConfiguration node,
        SourceDenyList deny,
        TimeProvider clock,
        ILogger<ItemSubmitter> log)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        _signWith = signWith ?? throw new ArgumentNullException(nameof(signWith));
        _patterns = patterns ?? throw new ArgumentNullException(nameof(patterns));
        _node = node ?? throw new ArgumentNullException(nameof(node));
        _deny = deny ?? throw new ArgumentNullException(nameof(deny));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        DropFolderFactory = root => new FileSystemArchiveDropFolder(root, clock);
    }

    /// <summary>The emitter under test (plan 08 A1): production is <see cref="EnvelopeWriter.Serialize"/>; one unit test substitutes a broken one.</summary>
    internal Func<Model, byte[]> Serializer { get; init; } = EnvelopeWriter.Serialize;

    /// <summary>Builds the drop folder over <c>archive.oneDriveFolder</c> (plan 41 A6): production is the file system; unit tests substitute it.</summary>
    internal Func<string, IArchiveDropFolder> DropFolderFactory { get; init; }

    /// <summary>
    /// Checks the file, then the signing key and the checkout (a pull), then picks the route — the bus with <c>--via bus</c> or without
    /// <c>archive.oneDriveFolder</c>, else the OneDrive folder, where the bytes are placed and verified before signing — then builds,
    /// signs, self-checks and publishes the item through <see cref="BusRepository.PublishItemAsync"/>. Never throws except for the
    /// caller's cancellation: a refused file is <see cref="ItemRefused"/> or <see cref="ItemUsage"/> with nothing signed, a refused
    /// self-check <see cref="SubmitRefused"/>, a key, store, lock or pull failure <see cref="SubmitFailed"/>, an unavailable folder
    /// <see cref="OneDriveUnavailable"/> (never a fallback to the bus), a failed placement <see cref="OneDriveWriteFailed"/> and different
    /// bytes in the folder <see cref="ItemRefused"/> <c>slug_taken (onedrive)</c> — each with nothing published.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The named outcome with the item's route, path, name, media type and size once known.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is null.</exception>
    public async Task<ItemSubmission> SubmitAsync(ItemRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        DateTimeOffset now = _clock.GetUtcNow();

        // 1. Every archive check the laptop can run, before anything leaves it (decision 1).
        ItemCheckResult checkedFile = ItemChecks.Check(request, _node.Archive, _deny, _patterns, DateOnly.FromDateTime(now.UtcDateTime));
        if (checkedFile.UsageError is { } usage)
        {
            return new ItemSubmission(new ItemUsage(usage), null, null, null, null, 0);
        }

        if (checkedFile.Candidate is not { } candidate)
        {
            LogRefused(ArchiveRefusalWire.ToWire(checkedFile.Refusal!.Value), checkedFile.Detail ?? string.Empty);
            return new ItemSubmission(new ItemRefused(checkedFile.Refusal.Value, checkedFile.Detail), null, null, null, null, 0);
        }

        string itemPath = request.Project.Value + "/" + candidate.DriveName;
        string mediaType = ArchiveMediaTypeWire.ToWire(candidate.Type);
        ItemSubmission Outcome(SubmitResult result, ItemRoute? route) => new(result, route, itemPath, request.Name, mediaType, candidate.Bytes.LongLength);

        // 2. The signing key, then the checkout: a pull before the route (plan 41 A7), so a lock or pull failure stops the call early.
        SigningKeyCheck key = await _bus.CheckSigningKeyAsync(_signWith, ct).ConfigureAwait(false);
        switch (key.State)
        {
            case SigningKeyState.Usable:
                break;
            case SigningKeyState.StoreUnavailable:
                string detail = key.Detail.StartsWith(StoreUnavailablePrefix, StringComparison.Ordinal) ? key.Detail[StoreUnavailablePrefix.Length..] : key.Detail;
                LogStoreUnavailable(detail);
                return Outcome(new SubmitFailed("secret-store", detail), null);
            default:
                return Outcome(new SubmitFailed("sign", key.Detail), null);
        }

        BusSyncResult pull = await _bus.PullAsync(ct).ConfigureAwait(false);
        if (!pull.Succeeded)
        {
            return Outcome(new SubmitFailed(pull.Detail.StartsWith(BusRepository.BusyDetail, StringComparison.Ordinal) ? "lock" : "pull", pull.Detail), null);
        }

        // 3. The route (spec 41 Decision Table "No access"): --via bus or no archive.oneDriveFolder is the bus, the folder untouched;
        //    a configured folder that is not available is a named failure, never a silent switch to the bus (decision 3).
        string? folder = _node.Archive.OneDriveFolder;
        ItemRoute route = request.ViaBus || folder is null ? ItemRoute.Bus : ItemRoute.OneDrive;
        if (route == ItemRoute.OneDrive)
        {
            IArchiveDropFolder drop = DropFolderFactory(folder!);
            DropProbe probe = drop.Probe();
            if (probe != DropProbe.Available)
            {
                string reason = DropProbeWire.ToWire(probe);
                LogOneDriveUnavailable(reason);
                return Outcome(new OneDriveUnavailable(reason, folder!), route);
            }

            // 4. Place and verify the bytes before signing: nothing is published for a file that is not in the folder.
            switch (drop.Place(request.Project, candidate.DriveName, candidate.Bytes))
            {
                case DropWritten or DropIdentical:
                    break;
                case DropTaken:
                    LogRefused(ArchiveRefusalWire.ToWire(ArchiveRefusal.SlugTaken), "onedrive");
                    return Outcome(new ItemRefused(ArchiveRefusal.SlugTaken, "onedrive"), route);
                case DropFailed failed:
                    LogOneDriveWriteFailed(failed.Detail);
                    return Outcome(new OneDriveWriteFailed(failed.Detail), route);
                case var other:
                    return Outcome(new OneDriveWriteFailed(other.GetType().Name), route);
            }
        }

        // 5. Build, sign, round-trip.
        BusIdentity me = _bus.Me;
        var header = new EnvelopeHeader(me.Tenant, EnvelopeId.New(now), me.Machine, MachineName.Central, now, EnvelopePriority.Normal);
        string? driveName = route == ItemRoute.OneDrive ? candidate.DriveName : null;
        var fields = new ItemFields(request.Project, request.Slug, request.Name, request.Description, candidate.SourceName, candidate.Bytes.LongLength, candidate.Sha256, route, driveName);
        Model item;
        try
        {
            item = Model.CreateItem(header, fields, route == ItemRoute.Bus ? candidate.Bytes : ReadOnlyMemory<byte>.Empty);
        }
        catch (ArgumentException ex)
        {
            LogRefusingRoundTrip(ex.ParamName ?? "envelope");
            return Outcome(new SubmitRefused(ex.ParamName ?? "envelope", ex.Message), route);
        }

        EnvelopeResult signed;
        try
        {
            signed = await _signer.SignAsync(item, _signWith, ct).ConfigureAwait(false);
        }
        catch (SecretStoreUnavailableException ex)
        {
            LogStoreUnavailable(ex.Message);
            return Outcome(new SubmitFailed("secret-store", ex.Message), route);
        }

        if (!signed.IsAccepted)
        {
            return Outcome(new SubmitFailed("sign", signed.Rejection!.Detail), route);
        }

        byte[] bytes = Serializer(signed.Envelope!);
        EnvelopeResult check = await _signer.ParseAndVerifyAsync(bytes, me.Tenant, ct).ConfigureAwait(false);
        string? differs = check.IsAccepted ? Differs(check.Envelope!, signed.Envelope!) : check.Rejection!.Field ?? "envelope";
        if (differs is not null)
        {
            LogRefusingRoundTrip(differs);
            return Outcome(new SubmitRefused(differs, check.Rejection?.Detail ?? "the serialised envelope does not read back as the request"), route);
        }

        // 6. Publish into this node's own items/ folder.
        Model envelope = signed.Envelope!;
        string id = header.Id.Value;
        string routeWire = EnvelopeWire.ToWire(route);
        string path = BusPaths.EnvelopeFile(BusPaths.Items(me.Tenant, me.Machine), header.Id);
        WriteOutcome outcome = await _bus.PublishItemAsync(envelope, ct).ConfigureAwait(false);
        switch (outcome)
        {
            case Pushed pushed:
                LogPublished(id, routeWire);
                return Outcome(new Submitted(header.Id, path, pushed.Attempts), route);
            case Deferred deferred:
                LogDeferred(id, routeWire, deferred.Detail);
                return Outcome(new SubmitDeferred(header.Id, path, deferred.Attempts, deferred.Detail), route);
            case WriteFailed failed:
                return Outcome(new SubmitFailed(failed.Stage, failed.Detail), route);
            default:
                return Outcome(new SubmitFailed("unknown", outcome.GetType().Name), route);
        }
    }

    /// <summary>The first field of the re-parsed item that differs from what was signed, or <see langword="null"/>.</summary>
    private static string? Differs(Model parsed, Model signed)
    {
        EnvelopeHeader h = parsed.Header;
        EnvelopeHeader s = signed.Header;
        ItemFields? p = parsed.Item;
        ItemFields i = signed.Item!;
        return (h, p) switch
        {
            _ when h.Id != s.Id => "id",
            _ when h.Tenant != s.Tenant => "tenant",
            _ when parsed.Type != EnvelopeType.Item || p is null => "type",
            _ when h.From != s.From => "from",
            _ when h.To != MachineName.Central => "to",
            _ when h.Created.ToUnixTimeSeconds() != s.Created.ToUnixTimeSeconds() => "created",
            _ when h.Priority != EnvelopePriority.Normal => "priority",
            _ when h.InReplyTo is not null => "in_reply_to",
            _ when p!.Via != i.Via => "via",
            _ when p!.Project != i.Project => "project",
            _ when p!.Slug != i.Slug => "slug",
            _ when !string.Equals(p!.DriveName, i.DriveName, StringComparison.Ordinal) => "drive_name",
            _ when !string.Equals(p!.Name, i.Name, StringComparison.Ordinal) => "name",
            _ when !string.Equals(p!.Description, i.Description, StringComparison.Ordinal) => "description",
            _ when !string.Equals(p!.SourceName, i.SourceName, StringComparison.Ordinal) => "source_name",
            _ when p!.Size != i.Size => "size",
            _ when !string.Equals(p!.Sha256, i.Sha256, StringComparison.Ordinal) => "sha256",
            _ when !parsed.Body.Span.SequenceEqual(signed.Body.Span) => "body",
            _ => parsed.FrontMatter.Entries.Keys.FirstOrDefault(k => !Section4ItemKeys.Contains(k)),
        };
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "refusing item: {Refusal} {Detail}")]
    private partial void LogRefused(string refusal, string detail);

    [LoggerMessage(Level = LogLevel.Information, Message = "published item {Id} to central via {Route}")]
    private partial void LogPublished(string id, string route);

    [LoggerMessage(Level = LogLevel.Warning, Message = "published item {Id} to central via {Route}: committed locally, not pushed ({Detail})")]
    private partial void LogDeferred(string id, string route, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "refusing item: the envelope does not round-trip ({Field})")]
    private partial void LogRefusingRoundTrip(string field);

    [LoggerMessage(Level = LogLevel.Warning, Message = "item not sent: the OneDrive folder is not available ({Reason})")]
    private partial void LogOneDriveUnavailable(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "item not sent: OneDrive write failed: {Detail}")]
    private partial void LogOneDriveWriteFailed(string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "refusing item: secret store unavailable: {Detail}")]
    private partial void LogStoreUnavailable(string detail);
}
