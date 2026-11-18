namespace Zyggy.Core.Envelope;

/// <summary>What a mandate lets Central close without asking the owner (deliverable 18, "The mandate"); wire strings via <see cref="EnvelopeWire"/>.</summary>
public enum MandateGrant
{
    /// <summary><c>accept</c>: accept a step whose checks passed.</summary>
    Accept,

    /// <summary><c>merge</c>: merge or push to a default branch.</summary>
    Merge,

    /// <summary><c>release</c>: cut a release.</summary>
    Release,

    /// <summary><c>deploy</c>: deploy.</summary>
    Deploy,
}

/// <summary>How a job reaches its machine (deliverable 18, the job field <c>deliver</c>); wire strings via <see cref="EnvelopeWire"/>.</summary>
public enum JobDelivery
{
    /// <summary><c>run</c> (the default, never written): the node service runs the job.</summary>
    Run,

    /// <summary><c>session</c>: a live session on the machine takes the job; the node service never runs it.</summary>
    Session,
}

/// <summary>The kinds of job Central may submit under a mandate (deliverable 18); wire strings via <see cref="EnvelopeWire"/>. Not an envelope field.</summary>
public enum MandateJobKind
{
    /// <summary><c>brief</c>: return the spec at the mandate's <c>spec_commit</c>.</summary>
    Brief,

    /// <summary><c>build</c>: build a step.</summary>
    Build,

    /// <summary><c>verify</c>: run the project's independent check on a step.</summary>
    Verify,

    /// <summary><c>correct</c>: bring a step back to the approved spec.</summary>
    Correct,

    /// <summary><c>merge</c>: needs the grant <see cref="MandateGrant.Merge"/>.</summary>
    Merge,

    /// <summary><c>release</c>: needs the grant <see cref="MandateGrant.Release"/>.</summary>
    Release,

    /// <summary><c>deploy</c>: needs the grant <see cref="MandateGrant.Deploy"/>.</summary>
    Deploy,
}

/// <summary>
/// The fields of a <c>mandate</c> envelope: what the owner pre-approved for one project on one machine (deliverable 18, "The mandate").
/// The envelope comes from Central, is addressed to the machine, and has an empty body.
/// </summary>
/// <param name="Project">The <c>project</c>: the registry name on that machine (mandatory).</param>
/// <param name="Goal">The <c>goal</c>: one line of 1–200 characters (mandatory).</param>
/// <param name="AllowedTools">The <c>allowed_tools</c> ceiling: each tool of a job must equal an entry, compared as text (mandatory).</param>
/// <param name="MaxJobs">The <c>max_jobs</c>: total jobs under the mandate, 1–200 (mandatory).</param>
/// <param name="Expires">The <c>expires</c> instant: after the envelope's <c>created</c> and at most 7 days after it (mandatory).</param>
/// <param name="Spec">The <c>spec</c>: a repository-relative path; present exactly when <paramref name="SpecCommit"/> is.</param>
/// <param name="SpecCommit">The <c>spec_commit</c>: the 40-character commit at which the owner approved the spec.</param>
/// <param name="Branches">The <c>branches</c> a job may start from and push: 0–5 branch names.</param>
/// <param name="MaxJobsPerHour">The <c>max_jobs_per_hour</c>: 1–60, 6 when absent.</param>
/// <param name="MaxJobsPerStep">The <c>max_jobs_per_step</c>: 1–20, 4 when absent.</param>
/// <param name="Grants">The <c>grants</c>: distinct members; <c>[accept]</c> when absent.</param>
/// <param name="Sessions">The <c>sessions</c> flag: tasks may be delivered into a live session; <see langword="false"/> when absent.</param>
public sealed record MandateFields(
    string Project,
    string Goal,
    IReadOnlyList<string> AllowedTools,
    int MaxJobs,
    DateTimeOffset Expires,
    string? Spec = null,
    string? SpecCommit = null,
    IReadOnlyList<string>? Branches = null,
    int MaxJobsPerHour = MandateFields.DefaultMaxJobsPerHour,
    int MaxJobsPerStep = MandateFields.DefaultMaxJobsPerStep,
    IReadOnlyList<MandateGrant>? Grants = null,
    bool Sessions = false)
{
    /// <summary>The <c>max_jobs_per_hour</c> of a mandate that does not state one.</summary>
    public const int DefaultMaxJobsPerHour = 6;

    /// <summary>The <c>max_jobs_per_step</c> of a mandate that does not state one.</summary>
    public const int DefaultMaxJobsPerStep = 4;

    /// <summary>Gets the branches a job may start from and push; never <see langword="null"/>.</summary>
    public IReadOnlyList<string> Branches { get; init; } = Branches ?? [];

    /// <summary>Gets the grants; never <see langword="null"/>, <c>[accept]</c> when none was given.</summary>
    public IReadOnlyList<MandateGrant> Grants { get; init; } = Grants ?? [MandateGrant.Accept];
}
