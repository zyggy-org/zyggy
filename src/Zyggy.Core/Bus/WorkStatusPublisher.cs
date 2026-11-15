using Microsoft.Extensions.Logging;

using Zyggy.Core.Configuration;
using Zyggy.Core.Envelope;
using Zyggy.Core.Memory;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Bus;

/// <summary>
/// The sender side of a status envelope (deliverable 18, "Live work is visible"): keeps only the fields the machine's
/// <c>supervision.detail</c> allows, refuses a value that matches a secret pattern before anything is signed, builds the envelope
/// addressed to Central from this node, signs it, proves the bytes read back as the request and writes it through
/// <see cref="BusRepository.PublishStatusAsync"/>. Writes nothing in an unattended run or when supervision is disabled.
/// <see cref="ContextSubmitter"/>'s shape.
/// </summary>
public sealed partial class WorkStatusPublisher
{
    private static readonly HashSet<string> StatusKeys =
    [
        "schema", "id", "tenant", "type", "from", "to", "created", "priority", "key_id", "sig",
        "project", "session", "since", "activity", "branch", "head", "dirty", "spec", "step",
    ];

    private readonly BusRepository _bus;
    private readonly EnvelopeSigner _signer;
    private readonly KeyId _signWith;
    private readonly SecretPatterns _patterns;
    private readonly WorkStatusOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<WorkStatusPublisher> _log;

    internal WorkStatusPublisher(BusRepository bus, EnvelopeSigner signer, KeyId signWith, SecretPatterns patterns, WorkStatusOptions options, TimeProvider clock, ILogger<WorkStatusPublisher> log)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        _signWith = signWith ?? throw new ArgumentNullException(nameof(signWith));
        _patterns = patterns ?? throw new ArgumentNullException(nameof(patterns));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Publishes the session's status, replacing its previous one. Never throws except for the caller's cancellation: every refusal and
    /// failure is a named result with nothing written.
    /// </summary>
    /// <param name="request">What the session's hook knows.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see cref="WorkStatusPublished"/>, <see cref="WorkStatusSkipped"/>, <see cref="WorkStatusRefused"/> or <see cref="WorkStatusFailed"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is null.</exception>
    public async Task<WorkStatusResult> PublishAsync(WorkStatusRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Skipped() is { } skipped)
        {
            return skipped;
        }

        StatusFields fields = _options.Detail == SupervisionDetail.Full
            ? new StatusFields(request.Project, request.Session, Seconds(request.Since), Seconds(request.Activity), request.Branch, request.Head, request.Dirty, request.Spec, request.Step)
            : new StatusFields(request.Project, request.Session, Seconds(request.Since), Seconds(request.Activity));
        foreach ((string field, string? value) in Texts(fields))
        {
            if (value is not null && _patterns.TryMatch(value, out string name))
            {
                LogRefusingPattern(field, name);
                return new WorkStatusRefused(field, "matches secret pattern " + name);
            }
        }

        BusIdentity me = _bus.Me;
        DateTimeOffset now = _clock.GetUtcNow();
        var header = new EnvelopeHeader(me.Tenant, EnvelopeId.New(now), me.Machine, MachineName.Central, now, EnvelopePriority.Normal);
        Model status;
        try
        {
            status = Model.CreateStatus(header, fields);
        }
        catch (ArgumentException ex)
        {
            LogRefusing(ex.ParamName ?? "envelope");
            return new WorkStatusRefused(ex.ParamName ?? "envelope", "breaks the status field rule");
        }

        Model signed;
        try
        {
            EnvelopeResult result = await _signer.SignAsync(status, _signWith, ct).ConfigureAwait(false);
            if (!result.IsAccepted)
            {
                return new WorkStatusFailed("sign", result.Rejection!.Detail);
            }

            signed = result.Envelope!;
            EnvelopeResult check = await _signer.ParseAndVerifyAsync(EnvelopeWriter.Serialize(signed), me.Tenant, ct).ConfigureAwait(false);
            if (!check.IsAccepted)
            {
                LogRefusing(check.Rejection!.Field ?? "envelope");
                return new WorkStatusRefused(check.Rejection.Field ?? "envelope", check.Rejection.Detail);
            }

            if (Differs(check.Envelope!, fields) is { } differing)
            {
                LogRefusing(differing);
                return new WorkStatusRefused(differing, "the serialised status does not read back as the request");
            }
        }
        catch (SecretStoreUnavailableException ex)
        {
            return new WorkStatusFailed("secret-store", ex.Message);
        }

        string path = BusPaths.EnvelopeFile(BusPaths.Status(me.Tenant, me.Machine), signed.Header.Id);
        return await _bus.PublishStatusAsync(signed, ct).ConfigureAwait(false) switch
        {
            Pushed => new WorkStatusPublished(signed.Header.Id, path, true),
            Deferred => new WorkStatusPublished(signed.Header.Id, path, false),
            WriteFailed failed => new WorkStatusFailed(failed.Stage, failed.Detail),
            WriteOutcome other => new WorkStatusFailed("unknown", other.GetType().Name),
        };
    }

    /// <summary>Removes the status of a session that ended. Never throws except for the caller's cancellation.</summary>
    /// <param name="session">The session's 8-character id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see cref="WorkStatusRemoved"/>, <see cref="WorkStatusSkipped"/> or <see cref="WorkStatusFailed"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="session"/> is null or empty.</exception>
    public async Task<WorkStatusResult> RemoveAsync(string session, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(session);
        if (Skipped() is { } skipped)
        {
            return skipped;
        }

        return await _bus.RemoveStatusAsync(session, ct).ConfigureAwait(false) switch
        {
            Pushed => new WorkStatusRemoved(true),
            Deferred => new WorkStatusRemoved(false),
            WriteFailed failed => new WorkStatusFailed(failed.Stage, failed.Detail),
            WriteOutcome other => new WorkStatusFailed("unknown", other.GetType().Name),
        };
    }

    private static DateTimeOffset Seconds(DateTimeOffset value) => DateTimeOffset.FromUnixTimeSeconds(value.ToUnixTimeSeconds());

    private static IEnumerable<(string Field, string? Value)> Texts(StatusFields fields) =>
    [
        ("project", fields.Project), ("session", fields.Session), ("branch", fields.Branch), ("head", fields.Head), ("spec", fields.Spec), ("step", fields.Step),
    ];

    /// <summary>The first thing in the re-parsed envelope that is not what was asked for, or <see langword="null"/>.</summary>
    private static string? Differs(Model parsed, StatusFields fields)
    {
        if (parsed.Type != EnvelopeType.Status || parsed.Status != fields)
        {
            return "status";
        }

        return parsed.FrontMatter.Entries.Keys.FirstOrDefault(k => !StatusKeys.Contains(k));
    }

    private WorkStatusSkipped? Skipped() =>
        _options.HooksOff ? new WorkStatusSkipped("hooks off")
        : !_options.Enabled ? new WorkStatusSkipped("supervision disabled")
        : null;

    [LoggerMessage(Level = LogLevel.Warning, Message = "refusing status: {Field} matches secret pattern {Name}")]
    private partial void LogRefusingPattern(string field, string name);

    [LoggerMessage(Level = LogLevel.Warning, Message = "refusing status: {Field} breaks its rule")]
    private partial void LogRefusing(string field);
}
