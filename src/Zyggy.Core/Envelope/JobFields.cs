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
public sealed record JobFields(
    string Project,
    string? Agent = null,
    bool Worktree = false,
    IReadOnlyList<string>? AllowedTools = null,
    IReadOnlyList<string>? ReportBack = null,
    int? TimeoutMinutes = null,
    DateTimeOffset? Deadline = null,
    int Attempt = 1);
