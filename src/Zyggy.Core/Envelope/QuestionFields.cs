namespace Zyggy.Core.Envelope;

/// <summary>
/// The fields of a <c>question</c> envelope: what an agent asks Central while it works (deliverable 18, "Questions and decisions").
/// The question itself is the body, 1–600 characters. Exactly one of <paramref name="Session"/> and <paramref name="Job"/> names the asker.
/// </summary>
/// <param name="Project">The <c>project</c>: the registry name, 1–100 characters of <c>[a-z0-9-]</c> (mandatory).</param>
/// <param name="Default">The <c>default</c>: one line of 1–200 characters saying what the agent does when no answer comes (mandatory).</param>
/// <param name="Options">The <c>options</c> the agent sees: 0–5 lines of 1–200 characters each.</param>
/// <param name="Session">The asking <c>session</c>: 8 characters of <c>[a-z0-9]</c>.</param>
/// <param name="Job">The asking <c>job</c>: the id of the job run.</param>
public sealed record QuestionFields(string Project, string Default, IReadOnlyList<string> Options, string? Session = null, EnvelopeId? Job = null);

/// <summary>What a <c>decision</c> does (deliverable 18, the decision classes); wire strings via <see cref="EnvelopeWire"/>.</summary>
public enum DecisionKind
{
    /// <summary><c>answer</c>: an answer to a question.</summary>
    Answer,

    /// <summary><c>accept</c>: a step whose checks passed is accepted.</summary>
    Accept,

    /// <summary><c>correct</c>: work is sent back to be brought in line with the approved spec.</summary>
    Correct,

    /// <summary><c>next</c>: the next step is started.</summary>
    Next,

    /// <summary><c>stop</c>: a mandate ended; the body is its summary.</summary>
    Stop,

    /// <summary><c>escalate</c>: the owner decides.</summary>
    Escalate,

    /// <summary><c>refuse</c>: never allowed.</summary>
    Refuse,

    /// <summary><c>resume</c>: the owner lifted a pause.</summary>
    Resume,
}

/// <summary>What a <c>decision</c> rests on (deliverable 18, honest reporting); wire strings via <see cref="EnvelopeWire"/>.</summary>
public enum DecisionBasis
{
    /// <summary><c>verified</c>: Central read the result of a check that ran.</summary>
    Verified,

    /// <summary><c>reported</c>: Central only read an agent's account.</summary>
    Reported,
}

/// <summary>
/// The fields of a <c>decision</c> envelope: Central's answer and its record at once (deliverable 18). The header's
/// <c>in_reply_to</c> names the question, report, job or mandate it settles; the body says what was decided and why, 1–1200 characters.
/// </summary>
/// <param name="Kind">The <c>kind</c> (mandatory).</param>
/// <param name="Basis">The <c>basis</c> (mandatory).</param>
/// <param name="Undo">The <c>undo</c>: one line of 1–200 characters saying how to reverse it (mandatory).</param>
/// <param name="Mandate">The <c>mandate</c> the decision acts under.</param>
/// <param name="Project">The <c>project</c> concerned.</param>
/// <param name="Step">The <c>step</c> concerned: 1–60 characters of <c>[a-z0-9 -]</c>.</param>
/// <param name="Evidence">The <c>evidence</c>: 0–10 envelope ids or commit shas (7–40 lowercase hexadecimal characters).</param>
public sealed record DecisionFields(
    DecisionKind Kind,
    DecisionBasis Basis,
    string Undo,
    EnvelopeId? Mandate = null,
    string? Project = null,
    string? Step = null,
    IReadOnlyList<string>? Evidence = null);
