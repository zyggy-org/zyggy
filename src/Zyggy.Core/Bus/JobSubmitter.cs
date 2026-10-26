using Microsoft.Extensions.Logging;

using Zyggy.Core.Envelope;
using Zyggy.Core.Secrets;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Bus;

/// <summary>
/// The sender side of a job (founding spec §4 row 1; spec 08): builds the signed envelope from a <see cref="JobRequest"/>, proves by a
/// round trip — serialise, re-parse, re-verify, compare every field and the body — that the bytes read back as the request, and only
/// then hands them to <see cref="BusRepository.SubmitAsync"/>. The write rule and the signing code are untouched; this is a second caller.
/// </summary>
public sealed partial class JobSubmitter
{
    private static readonly HashSet<string> Section4Keys =
    [
        "schema", "id", "tenant", "type", "from", "to", "created", "in_reply_to", "priority", "project", "agent", "worktree",
        "allowed_tools", "report_back", "timeout_minutes", "deadline", "attempt", "key_id", "sig",
    ];

    private readonly BusRepository _bus;
    private readonly EnvelopeSigner _signer;
    private readonly KeyId _signWith;
    private readonly TimeProvider _clock;
    private readonly ILogger<JobSubmitter> _log;

    /// <summary>Creates the submitter for the node <paramref name="bus"/> belongs to.</summary>
    /// <param name="bus">The bus checkout; its identity is the sender.</param>
    /// <param name="signer">The envelope signer.</param>
    /// <param name="signWith">The key this machine signs with (<c>node.json</c> <c>signWith</c>).</param>
    /// <param name="clock">The clock (<c>created</c> and the id's timestamp).</param>
    /// <param name="log">The logger.</param>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    public JobSubmitter(BusRepository bus, EnvelopeSigner signer, KeyId signWith, TimeProvider clock, ILogger<JobSubmitter> log)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        _signWith = signWith ?? throw new ArgumentNullException(nameof(signWith));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>The emitter under test (plan 08 A1): production is <see cref="EnvelopeWriter.Serialize"/>; one unit test substitutes a broken one.</summary>
    internal Func<Model, byte[]> Serializer { get; init; } = EnvelopeWriter.Serialize;

    /// <summary>
    /// Signs <paramref name="request"/> and proves the bytes round-trip: parsed and verified again, every §4 field and the body equal
    /// the request, and no front-matter key is outside the §4 names. Touches the secret store (through the signer) and nothing else.
    /// </summary>
    /// <param name="request">The validated request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see cref="Built"/>, or <see cref="BuildRefused"/> naming the first field that differs.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is null.</exception>
    /// <exception cref="SigningFailedException">The key is absent or unusable (the caller maps it to <c>SubmitFailed("sign", …)</c>).</exception>
    public async Task<BuildResult> BuildAsync(JobRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        BusIdentity me = _bus.Me;
        DateTimeOffset now = _clock.GetUtcNow();
        var header = new EnvelopeHeader(me.Tenant, EnvelopeId.New(now), me.Machine, request.To, now, request.Priority);
        var fields = new JobFields(request.Project.Value, request.Agent, Worktree: false, request.AllowedTools, ReportBack: null, request.TimeoutMinutes, Deadline: null, Attempt: 1);
        Model job;
        try
        {
            job = Model.CreateJob(header, fields, request.Body);
        }
        catch (ArgumentException ex)
        {
            return new BuildRefused(ex.ParamName ?? "envelope", ex.Message);
        }

        EnvelopeResult signed = await _signer.SignAsync(job, _signWith, ct).ConfigureAwait(false);
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

        if (Differs(check.Envelope!, signed.Envelope!, request) is { } field)
        {
            return new BuildRefused(field, "the serialised envelope does not read back as the request");
        }

        LogBuilt(header.Id.Value, request.To.Value);
        return new Built(signed.Envelope!, bytes);
    }

    /// <summary>
    /// Builds, self-checks and writes the job through <see cref="BusRepository.SubmitAsync"/> (the §4 write rule, unchanged, with its
    /// submission branch). Never throws except for the caller's cancellation: a refused self-check is <see cref="SubmitRefused"/> with
    /// nothing written; an absent or unusable key is <see cref="SubmitFailed"/> at stage <c>sign</c> with nothing written.
    /// </summary>
    /// <param name="request">The validated request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The named outcome.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is null.</exception>
    public async Task<SubmitResult> SubmitAsync(JobRequest request, CancellationToken ct)
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

        if (built is BuildRefused refused)
        {
            LogRefusing(refused.Field);
            return new SubmitRefused(refused.Field, refused.Detail);
        }

        Model envelope = ((Built)built).Envelope;
        string id = envelope.Header.Id.Value;
        string path = BusPaths.EnvelopeFile(BusPaths.Jobs(_bus.Me.Tenant, request.To), envelope.Header.Id);
        WriteOutcome outcome = await _bus.SubmitAsync(envelope, ct).ConfigureAwait(false);
        switch (outcome)
        {
            case Pushed pushed:
                LogSubmitted(id, request.To.Value);
                return new Submitted(envelope.Header.Id, path, pushed.Attempts);
            case Deferred deferred:
                LogDeferred(id, request.To.Value, deferred.Detail);
                return new SubmitDeferred(envelope.Header.Id, path, deferred.Attempts, deferred.Detail);
            case WriteFailed failed:
                return new SubmitFailed(failed.Stage, failed.Detail);
            default:
                return new SubmitFailed("unknown", outcome.GetType().Name);
        }
    }

    /// <summary>The first field of the re-parsed envelope that differs from what was signed and requested, or <see langword="null"/>.</summary>
    private static string? Differs(Model parsed, Model signed, JobRequest request)
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

        if (parsed.Type != EnvelopeType.Job)
        {
            return "type";
        }

        if (h.From != s.From)
        {
            return "from";
        }

        if (h.To != request.To)
        {
            return "to";
        }

        if (h.Created.ToUnixTimeSeconds() != s.Created.ToUnixTimeSeconds())
        {
            return "created";
        }

        if (h.Priority != request.Priority)
        {
            return "priority";
        }

        JobFields? j = parsed.Job;
        if (j is null || !string.Equals(j.Project, request.Project.Value, StringComparison.Ordinal))
        {
            return "project";
        }

        if (!string.Equals(j.Agent, request.Agent, StringComparison.Ordinal))
        {
            return "agent";
        }

        if (j.Worktree)
        {
            return "worktree";
        }

        if (!SameList(j.AllowedTools, request.AllowedTools))
        {
            return "allowed_tools";
        }

        if (j.ReportBack is not null)
        {
            return "report_back";
        }

        if (j.TimeoutMinutes != request.TimeoutMinutes)
        {
            return "timeout_minutes";
        }

        if (j.Deadline is not null)
        {
            return "deadline";
        }

        if (j.Attempt != 1)
        {
            return "attempt";
        }

        if (!parsed.Body.Span.SequenceEqual(request.Body.Span))
        {
            return "body";
        }

        return parsed.FrontMatter.Entries.Keys.FirstOrDefault(k => !Section4Keys.Contains(k));
    }

    private static bool SameList(IReadOnlyList<string>? left, IReadOnlyList<string>? right) =>
        left is null ? right is null : right is not null && left.SequenceEqual(right, StringComparer.Ordinal);

    [LoggerMessage(Level = LogLevel.Debug, Message = "built job {Id} to {To}")]
    private partial void LogBuilt(string id, string to);

    [LoggerMessage(Level = LogLevel.Information, Message = "submitted job {Id} to {To}")]
    private partial void LogSubmitted(string id, string to);

    [LoggerMessage(Level = LogLevel.Warning, Message = "submitted job {Id} to {To}: committed locally, not pushed ({Detail})")]
    private partial void LogDeferred(string id, string to, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "refusing to submit: the envelope does not round-trip ({Field})")]
    private partial void LogRefusing(string field);

    [LoggerMessage(Level = LogLevel.Warning, Message = "refusing to submit: secret store unavailable: {Detail}")]
    private partial void LogStoreUnavailable(string detail);
}

/// <summary>The signing key is absent or unusable: not a refusal of the request but a failure of this machine's configuration.</summary>
public sealed class SigningFailedException : Exception
{
    /// <summary>Creates the exception.</summary>
    public SigningFailedException()
    {
    }

    /// <summary>Creates the exception with the signer's redacted detail.</summary>
    /// <param name="message">The detail.</param>
    public SigningFailedException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a cause.</summary>
    /// <param name="message">The detail.</param>
    /// <param name="innerException">The cause.</param>
    public SigningFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
