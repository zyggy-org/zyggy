using System.Text;

using Microsoft.Extensions.Logging;

using Zyggy.Core.Envelope;
using Zyggy.Core.Memory;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Bus;

/// <summary>What an agent asks Central (deliverable 18, "Questions and decisions").</summary>
/// <param name="Project">The project's registry name.</param>
/// <param name="Text">The question, 1–600 characters.</param>
/// <param name="Default">One line: what the agent does when no answer comes.</param>
/// <param name="Options">The options the agent sees, 0–5 lines.</param>
/// <param name="Session">The asking session's 8-character id, from a session.</param>
/// <param name="Job">The asking job's id, from a job run.</param>
public sealed record QuestionRequest(string Project, string Text, string Default, IReadOnlyList<string> Options, string? Session = null, EnvelopeId? Job = null);

/// <summary>What Central decides (deliverable 18, "Questions and decisions").</summary>
/// <param name="InReplyTo">The question, report, job or mandate the decision settles.</param>
/// <param name="To">The machine concerned.</param>
/// <param name="Kind">What the decision does.</param>
/// <param name="Basis">What it rests on.</param>
/// <param name="Text">What was decided and why, 1–1200 characters.</param>
/// <param name="Undo">One line: how to reverse it, or that there is nothing to undo.</param>
/// <param name="Mandate">The mandate it acts under, when there is one.</param>
/// <param name="Project">The project concerned.</param>
/// <param name="Step">The step concerned.</param>
/// <param name="Evidence">Envelope ids and commit shas.</param>
public sealed record DecisionRequest(
    EnvelopeId InReplyTo,
    MachineName To,
    DecisionKind Kind,
    DecisionBasis Basis,
    string Text,
    string Undo,
    EnvelopeId? Mandate = null,
    string? Project = null,
    string? Step = null,
    IReadOnlyList<string>? Evidence = null);

/// <summary>What <see cref="QuestionAsker"/> or <see cref="DecisionWriter"/> did.</summary>
public abstract record AskDecideResult
{
    private protected AskDecideResult()
    {
    }
}

/// <summary>The envelope is on the bus checkout, committed.</summary>
/// <param name="Id">The envelope's id.</param>
/// <param name="Path">Its path in the checkout.</param>
/// <param name="Pushed"><see langword="false"/> when the commit stays local until the next write.</param>
public sealed record AskDecidePublished(EnvelopeId Id, string Path, bool Pushed) : AskDecideResult;

/// <summary>Nothing was written, on purpose.</summary>
/// <param name="Why">The reason, for example <c>supervision disabled</c>.</param>
public sealed record AskDecideSkipped(string Why) : AskDecideResult;

/// <summary>A value breaks its rule or matches a secret pattern, or this machine may not write it; nothing was signed or written.</summary>
/// <param name="Field">The field at fault (<c>body</c> for the text, <c>machine</c> for a decision off Central).</param>
/// <param name="Detail">Why; for a secret pattern, <c>matches secret pattern &lt;name&gt;</c>.</param>
public sealed record AskDecideRefused(string Field, string Detail) : AskDecideResult;

/// <summary>Signing or the bus write failed; nothing is on the bus.</summary>
/// <param name="Stage">The stage that failed.</param>
/// <param name="Detail">The failure text.</param>
public sealed record AskDecideFailed(string Stage, string Detail) : AskDecideResult;

/// <summary>
/// The sender side of a question (deliverable 18): refuses text that matches a secret pattern before anything is signed, builds the
/// envelope addressed to Central from this node, signs it, proves it reads back, and writes it into this node's own
/// <c>questions/</c> folder. Never throws except for the caller's cancellation.
/// </summary>
public sealed partial class QuestionAsker
{
    private readonly BusRepository _bus;
    private readonly EnvelopeSigner _signer;
    private readonly KeyId _signWith;
    private readonly SecretPatterns _patterns;
    private readonly bool _enabled;
    private readonly TimeProvider _clock;
    private readonly ILogger<QuestionAsker> _log;

    internal QuestionAsker(BusRepository bus, EnvelopeSigner signer, KeyId signWith, SecretPatterns patterns, bool enabled, TimeProvider clock, ILogger<QuestionAsker> log)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        _signWith = signWith ?? throw new ArgumentNullException(nameof(signWith));
        _patterns = patterns ?? throw new ArgumentNullException(nameof(patterns));
        _enabled = enabled;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>Writes the question to the bus.</summary>
    /// <param name="request">The question.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see cref="AskDecidePublished"/>, <see cref="AskDecideSkipped"/>, <see cref="AskDecideRefused"/> or <see cref="AskDecideFailed"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is null.</exception>
    public async Task<AskDecideResult> AskAsync(QuestionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_enabled)
        {
            return new AskDecideSkipped("supervision disabled");
        }

        IEnumerable<(string Field, string Value)> texts = [("body", request.Text), ("default", request.Default), .. request.Options.Select(o => ("options", o))];
        if (AskDecideSupport.FirstSecret(_patterns, texts) is { } secret)
        {
            LogRefusingPattern(secret.Field, secret.Name);
            return new AskDecideRefused(secret.Field, "matches secret pattern " + secret.Name);
        }

        BusIdentity me = _bus.Me;
        DateTimeOffset now = _clock.GetUtcNow();
        var header = new EnvelopeHeader(me.Tenant, EnvelopeId.New(now), me.Machine, MachineName.Central, now, EnvelopePriority.Normal);
        (Model? signed, AskDecideResult? stopped) = await AskDecideSupport.SignAsync(
            _signer,
            _signWith,
            me.Tenant,
            () => Model.CreateQuestion(header, new QuestionFields(request.Project, request.Default, request.Options, request.Session, request.Job), AskDecideSupport.Body(request.Text)),
            ct).ConfigureAwait(false);
        if (signed is null)
        {
            return stopped!;
        }

        string path = BusPaths.EnvelopeFile(BusPaths.Questions(me.Tenant, me.Machine), signed.Header.Id);
        return AskDecideSupport.Outcome(await _bus.PublishQuestionAsync(signed, ct).ConfigureAwait(false), signed.Header.Id, path);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "refusing question: {Field} matches secret pattern {Name}")]
    private partial void LogRefusingPattern(string field, string name);
}

/// <summary>
/// The sender side of a decision (deliverable 18): only on the machine named Central; refuses text that matches a secret pattern
/// before anything is signed, builds the envelope from Central, signs it, proves it reads back, and writes it into Central's
/// <c>decisions/</c> folder, archiving the answered question in the same commit. Never throws except for the caller's cancellation.
/// </summary>
public sealed partial class DecisionWriter
{
    private readonly BusRepository _bus;
    private readonly EnvelopeSigner _signer;
    private readonly KeyId _signWith;
    private readonly SecretPatterns _patterns;
    private readonly TimeProvider _clock;
    private readonly ILogger<DecisionWriter> _log;

    internal DecisionWriter(BusRepository bus, EnvelopeSigner signer, KeyId signWith, SecretPatterns patterns, TimeProvider clock, ILogger<DecisionWriter> log)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        _signWith = signWith ?? throw new ArgumentNullException(nameof(signWith));
        _patterns = patterns ?? throw new ArgumentNullException(nameof(patterns));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>Writes the decision to the bus.</summary>
    /// <param name="request">The decision.</param>
    /// <param name="answeredQuestion">The verified question it answers, or <see langword="null"/> when it answers something else.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see cref="AskDecidePublished"/>, <see cref="AskDecideRefused"/> or <see cref="AskDecideFailed"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is null.</exception>
    public async Task<AskDecideResult> DecideAsync(DecisionRequest request, Model? answeredQuestion, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        BusIdentity me = _bus.Me;
        if (me.Machine != MachineName.Central)
        {
            return new AskDecideRefused("machine", "only central decides");
        }

        if (answeredQuestion is not null && (answeredQuestion.Type != EnvelopeType.Question || answeredQuestion.Header.Id != request.InReplyTo))
        {
            return new AskDecideRefused("in_reply_to", "is not the question given");
        }

        if (AskDecideSupport.FirstSecret(_patterns, [("body", request.Text), ("undo", request.Undo)]) is { } secret)
        {
            LogRefusingPattern(secret.Field, secret.Name);
            return new AskDecideRefused(secret.Field, "matches secret pattern " + secret.Name);
        }

        DateTimeOffset now = _clock.GetUtcNow();
        var header = new EnvelopeHeader(me.Tenant, EnvelopeId.New(now), me.Machine, request.To, now, EnvelopePriority.Normal, request.InReplyTo);
        (Model? signed, AskDecideResult? stopped) = await AskDecideSupport.SignAsync(
            _signer,
            _signWith,
            me.Tenant,
            () => Model.CreateDecision(header, new DecisionFields(request.Kind, request.Basis, request.Undo, request.Mandate, request.Project, request.Step, request.Evidence), AskDecideSupport.Body(request.Text)),
            ct).ConfigureAwait(false);
        if (signed is null)
        {
            return stopped!;
        }

        string path = BusPaths.EnvelopeFile(BusPaths.Decisions(me.Tenant, me.Machine), signed.Header.Id);
        return AskDecideSupport.Outcome(await _bus.PublishDecisionAsync(signed, answeredQuestion, ct).ConfigureAwait(false), signed.Header.Id, path);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "refusing decision: {Field} matches secret pattern {Name}")]
    private partial void LogRefusingPattern(string field, string name);
}

/// <summary>What the two senders share: the secret check, the body form, the sign-and-read-back step and the outcome mapping.</summary>
internal static class AskDecideSupport
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>The first text that matches a secret pattern, with its field and the pattern's name.</summary>
    public static (string Field, string Name)? FirstSecret(SecretPatterns patterns, IEnumerable<(string Field, string Value)> texts)
    {
        foreach ((string field, string value) in texts)
        {
            if (patterns.TryMatch(value, out string name))
            {
                return (field, name);
            }
        }

        return null;
    }

    /// <summary>The text as a body: line endings normalised, exactly one final newline.</summary>
    public static byte[] Body(string text) =>
        Utf8.GetBytes(text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\r', '\n') + "\n");

    /// <summary>Builds, signs and re-reads the envelope; a refusal or failure comes back as the result with no envelope.</summary>
    public static async Task<(Model? Signed, AskDecideResult? Stopped)> SignAsync(EnvelopeSigner signer, KeyId signWith, TenantId tenant, Func<Model> create, CancellationToken ct)
    {
        Model envelope;
        try
        {
            envelope = create();
        }
        catch (ArgumentException ex)
        {
            return (null, new AskDecideRefused(ex.ParamName ?? "envelope", "breaks its rule"));
        }

        try
        {
            EnvelopeResult signed = await signer.SignAsync(envelope, signWith, ct).ConfigureAwait(false);
            if (!signed.IsAccepted)
            {
                return (null, new AskDecideFailed("sign", signed.Rejection!.Detail));
            }

            EnvelopeResult check = await signer.ParseAndVerifyAsync(EnvelopeWriter.Serialize(signed.Envelope!), tenant, ct).ConfigureAwait(false);
            return check.IsAccepted
                ? (signed.Envelope, null)
                : (null, new AskDecideRefused(check.Rejection!.Field ?? "envelope", check.Rejection.Detail));
        }
        catch (SecretStoreUnavailableException ex)
        {
            return (null, new AskDecideFailed("secret-store", ex.Message));
        }
    }

    public static AskDecideResult Outcome(WriteOutcome outcome, EnvelopeId id, string path) => outcome switch
    {
        Pushed => new AskDecidePublished(id, path, true),
        Deferred => new AskDecidePublished(id, path, false),
        WriteFailed failed => new AskDecideFailed(failed.Stage, failed.Detail),
        _ => new AskDecideFailed("unknown", outcome.GetType().Name),
    };
}
