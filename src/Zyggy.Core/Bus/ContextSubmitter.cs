using Microsoft.Extensions.Logging;

using Zyggy.Core.Envelope;
using Zyggy.Core.Memory;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Bus;

/// <summary>
/// The sender side of a context envelope (founding spec §4 as amended by deliverable 12, D1): scans every body line against the
/// secret patterns before anything is signed, builds the envelope addressed to Central from this node, signs it and proves by a
/// round trip — serialise, re-parse, re-verify, compare every §4 context field and the body — that the bytes read back as the
/// request. <see cref="JobSubmitter"/>'s shape; a second caller of the one write rule.
/// </summary>
public sealed partial class ContextSubmitter
{
    private static readonly HashSet<string> Section4ContextKeys =
    [
        "schema", "id", "tenant", "type", "from", "to", "created", "in_reply_to", "priority", "scope", "key_id", "sig",
    ];

    private readonly BusRepository _bus;
    private readonly EnvelopeSigner _signer;
    private readonly KeyId _signWith;
    private readonly SecretPatterns _patterns;
    private readonly ContextEnvelopeOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<ContextSubmitter> _log;

    internal ContextSubmitter(BusRepository bus, EnvelopeSigner signer, KeyId signWith, SecretPatterns patterns, ContextEnvelopeOptions options, TimeProvider clock, ILogger<ContextSubmitter> log)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        _signWith = signWith ?? throw new ArgumentNullException(nameof(signWith));
        _patterns = patterns ?? throw new ArgumentNullException(nameof(patterns));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>The emitter under test (plan 08 A1): production is <see cref="EnvelopeWriter.Serialize"/>; one unit test substitutes a broken one.</summary>
    internal Func<Model, byte[]> Serializer { get; init; } = EnvelopeWriter.Serialize;

    /// <summary>
    /// Refuses a secret-shaped line or an oversize body, then signs <paramref name="request"/> and proves the bytes round-trip.
    /// Touches the secret store (through the signer) and nothing else.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see cref="Built"/>, <see cref="BuildRefusedByPattern"/>, or <see cref="BuildRefused"/> naming the first field that differs.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is null.</exception>
    /// <exception cref="SigningFailedException">The key is absent or unusable (the caller maps it to <c>SubmitFailed("sign", …)</c>).</exception>
    public async Task<BuildResult> BuildAsync(ContextRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        foreach (var line in request.Lines)
        {
            if (_patterns.TryMatch(line, out var name))
            {
                LogRefusingPattern(name);
                return new BuildRefusedByPattern(name);
            }
        }

        byte[] body = request.Body();
        if (body.Length > _options.MaxBodyBytes)
        {
            return new BuildRefused("body", "body is longer than 64 KiB");
        }

        BusIdentity me = _bus.Me;
        DateTimeOffset now = _clock.GetUtcNow();
        var header = new EnvelopeHeader(me.Tenant, EnvelopeId.New(now), me.Machine, MachineName.Central, now, EnvelopePriority.Normal);
        Model context;
        try
        {
            context = Model.CreateContext(header, new ContextFields(request.Scope), body);
        }
        catch (ArgumentException ex)
        {
            return new BuildRefused(ex.ParamName ?? "envelope", ex.Message);
        }

        EnvelopeResult signed = await _signer.SignAsync(context, _signWith, ct).ConfigureAwait(false);
        if (!signed.IsAccepted)
        {
            throw new SigningFailedException(signed.Rejection!.Detail);
        }

        byte[] bytes = Serializer(signed.Envelope!);
        EnvelopeResult check = await _signer.ParseAndVerifyAsync(bytes, me.Tenant, ct).ConfigureAwait(false);
        if (!check.IsAccepted)
        {
            return new BuildRefused(check.Rejection!.Field ?? "envelope", check.Rejection.Detail);
        }

        if (Differs(check.Envelope!, signed.Envelope!, request, body) is { } field)
        {
            return new BuildRefused(field, "the serialised envelope does not read back as the request");
        }

        LogBuilt(header.Id.Value);
        return new Built(signed.Envelope!, bytes);
    }

    /// <summary>
    /// Builds, self-checks and publishes the context through <see cref="BusRepository.PublishContextAsync"/>. Never throws except for
    /// the caller's cancellation: a secret-shaped line is <see cref="ContextRefused"/>, a refused self-check <see cref="SubmitRefused"/>,
    /// an absent or unusable key <see cref="SubmitFailed"/> at stage <c>sign</c> — each with nothing written.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The named outcome.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is null.</exception>
    public async Task<SubmitResult> SubmitAsync(ContextRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        BuildResult built;
        try
        {
            built = await BuildAsync(request, ct).ConfigureAwait(false);
        }
        catch (SigningFailedException ex)
        {
            return new SubmitFailed("sign", ex.Message);
        }
        catch (SecretStoreUnavailableException ex)
        {
            LogStoreUnavailable(ex.Message);
            return new SubmitFailed("secret-store", ex.Message);
        }

        switch (built)
        {
            case BuildRefusedByPattern byPattern:
                return new ContextRefused(byPattern.PatternName);
            case BuildRefused refused:
                LogRefusing(refused.Field);
                return new SubmitRefused(refused.Field, refused.Detail);
            default:
                break;
        }

        Model envelope = ((Built)built).Envelope;
        string id = envelope.Header.Id.Value;
        string path = BusPaths.EnvelopeFile(BusPaths.Context(_bus.Me.Tenant, _bus.Me.Machine), envelope.Header.Id);
        WriteOutcome outcome = await _bus.PublishContextAsync(envelope, ct).ConfigureAwait(false);
        switch (outcome)
        {
            case Pushed pushed:
                LogPublished(id);
                return new Submitted(envelope.Header.Id, path, pushed.Attempts);
            case Deferred deferred:
                LogDeferred(id, deferred.Detail);
                return new SubmitDeferred(envelope.Header.Id, path, deferred.Attempts, deferred.Detail);
            case WriteFailed failed:
                return new SubmitFailed(failed.Stage, failed.Detail);
            default:
                return new SubmitFailed("unknown", outcome.GetType().Name);
        }
    }

    /// <summary>The first field of the re-parsed envelope that differs from what was signed and requested, or <see langword="null"/>.</summary>
    private static string? Differs(Model parsed, Model signed, ContextRequest request, byte[] body)
    {
        EnvelopeHeader h = parsed.Header;
        EnvelopeHeader s = signed.Header;
        if (h.Id != s.Id)
        {
            return "id";
        }

        if (h.Tenant != s.Tenant)
        {
            return "tenant";
        }

        if (parsed.Type != EnvelopeType.Context)
        {
            return "type";
        }

        if (h.From != s.From)
        {
            return "from";
        }

        if (h.To != MachineName.Central)
        {
            return "to";
        }

        if (h.Created.ToUnixTimeSeconds() != s.Created.ToUnixTimeSeconds())
        {
            return "created";
        }

        if (h.Priority != EnvelopePriority.Normal)
        {
            return "priority";
        }

        if (h.InReplyTo is not null)
        {
            return "in_reply_to";
        }

        if (parsed.Context is null || parsed.Context.Scope != request.Scope)
        {
            return "scope";
        }

        if (!parsed.Body.Span.SequenceEqual(body))
        {
            return "body";
        }

        return parsed.FrontMatter.Entries.Keys.FirstOrDefault(k => !Section4ContextKeys.Contains(k));
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "built context {Id}")]
    private partial void LogBuilt(string id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "refusing context: matches secret pattern {Name}")]
    private partial void LogRefusingPattern(string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "published context {Id} to central")]
    private partial void LogPublished(string id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "published context {Id} to central: committed locally, not pushed ({Detail})")]
    private partial void LogDeferred(string id, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "refusing context: the envelope does not round-trip ({Field})")]
    private partial void LogRefusing(string field);

    [LoggerMessage(Level = LogLevel.Warning, Message = "refusing context: secret store unavailable: {Detail}")]
    private partial void LogStoreUnavailable(string detail);
}
