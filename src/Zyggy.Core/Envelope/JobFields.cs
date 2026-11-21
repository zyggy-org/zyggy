namespace Zyggy.Core.Envelope;

/// <summary>The fields of a <c>job</c> envelope (founding spec §4 "Mandatory fields per type", row <c>job</c>).</summary>
/// <param name="Project">The <c>project</c> registry name or absolute path (mandatory).</param>
/// <param name="Agent">The optional <c>agent</c> (subagent name).</param>
/// <param name="Worktree">The <c>worktree</c> flag; <see langword="false"/> when absent.</param>
/// <param name="AllowedTools">The <c>allowed_tools</c> list; <see langword="null"/> when absent (the policy default applies).</param>
/// <param name="ReportBack">The <c>report_back</c> list; <see langword="null"/> when absent.</param>
/// <param name="TimeoutMinutes">The <c>timeout_minutes</c>; <see langword="null"/> when absent (the node default applies).</param>
/// <param name="Deadline">The optional <c>deadline</c>, in UTC.</param>
/// <param name="Attempt">The <c>attempt</c> counter; 1 when absent.</param>
/// <param name="Branch">
/// The optional <c>branch</c> the job runs on and pushes (spec 53): a git branch name of the §4 shape (1–100 characters of <c>[A-Za-z0-9._/-]</c>, see spec 53)
/// checks, only with <c>worktree: true</c>; <see langword="null"/> runs on <c>zyggy/&lt;id&gt;</c> as before.
/// </param>
/// <param name="Description">
/// The optional one-line <c>description</c> of what the branch holds after this job (spec 53); data, never shown to the model.
/// </param>
/// <param name="Mandate">The <c>mandate</c> the job runs under (deliverable 18); <see langword="null"/> for a job the owner gave a go for.</param>
/// <param name="Step">The <c>step</c> the job belongs to: 1–60 characters of <c>[a-z0-9 -]</c>.</param>
/// <param name="Base">The <c>base</c> branch a worktree job starts from and pushes back to.</param>
/// <param name="Deliver">The <c>deliver</c> mode; <see cref="JobDelivery.Run"/> when absent, and then never written.</param>
/// <param name="Kind">The <c>kind</c> (spec 59); <see cref="JobKind.Model"/> when absent, and then never written.</param>
/// <param name="FetchPath">
/// The <c>fetch_path</c> of a <see cref="JobKind.Fetch"/> job: a repository-relative path with <c>/</c> separators (mandatory there,
/// refused on any other kind). Its safety (no <c>..</c>, not absolute) is the node's check, answered with <c>schema_unsupported</c>.
/// </param>
/// <param name="FetchRef">The <c>fetch_ref</c> branch a fetch reads as <c>origin/&lt;ref&gt;</c>; <see langword="null"/> reads the project's default branch.</param>
/// <param name="Command">
/// The <c>command</c> name of a <see cref="JobKind.Command"/> job (<c>[a-z0-9-]</c>; mandatory there, refused on any other kind), looked up
/// in the node's own <c>node.json</c> <c>commands</c>; no text of the envelope reaches the process.
/// </param>
/// <param name="MaxTurns">
/// The run's turn limit (spec 59 slice 6; a model job only): at least 1, and never above the node's <c>claude.maxTurns</c>, which the
/// node applies instead and says so in the report; <see langword="null"/> runs with the node's value.
/// </param>
/// <param name="Continue">
/// The id of an earlier job whose branch <c>zyggy/&lt;id&gt;</c> on the origin is this run's start point (spec 59 slice 6); only with
/// <c>worktree: true</c> on a model job. A branch not on the origin is <c>rejected</c> / <c>branch_refused</c>.
/// </param>
public sealed record JobFields(
    string Project,
    string? Agent = null,
    bool Worktree = false,
    IReadOnlyList<string>? AllowedTools = null,
    IReadOnlyList<string>? ReportBack = null,
    int? TimeoutMinutes = null,
    DateTimeOffset? Deadline = null,
    int Attempt = 1,
    string? Branch = null,
    string? Description = null,
    EnvelopeId? Mandate = null,
    string? Step = null,
    string? Base = null,
    JobDelivery Deliver = JobDelivery.Run,
    JobKind Kind = JobKind.Model,
    string? FetchPath = null,
    string? FetchRef = null,
    string? Command = null,
    int? MaxTurns = null,
    EnvelopeId? Continue = null);
