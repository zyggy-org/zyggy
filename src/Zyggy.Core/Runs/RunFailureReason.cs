namespace Zyggy.Core.Runs;

/// <summary>
/// Why a run failed: the closed reason list of founding spec §9. Wire strings come from
/// <see cref="RunFailureReasonWire"/> only.
/// </summary>
public enum RunFailureReason
{
    /// <summary>The job names a project the machine does not know (<c>unknown_project</c>).</summary>
    UnknownProject,

    /// <summary>The job names an agent the machine does not know (<c>unknown_agent</c>).</summary>
    UnknownAgent,

    /// <summary>Another run holds the lock (<c>locked</c>).</summary>
    Locked,

    /// <summary>The run exceeded its time limit (<c>timeout</c>).</summary>
    Timeout,

    /// <summary>Output was withheld by the data-loss filter (<c>dlp_filter</c>).</summary>
    DlpFilter,

    /// <summary>The model process failed (<c>claude_error</c>).</summary>
    ClaudeError,

    /// <summary>A git operation failed (<c>git_error</c>).</summary>
    GitError,

    /// <summary>The input's schema version is not supported (<c>schema_unsupported</c>).</summary>
    SchemaUnsupported,

    /// <summary>The monthly budget is exhausted (<c>budget_exceeded</c>; reserved, never emitted in v1, §11).</summary>
    BudgetExceeded,

    /// <summary>
    /// A job's named branch the node must not touch (<c>branch_refused</c>, deliverable 53): it is the project's default branch, or
    /// the job also carries spec 18's <c>base</c> or <c>mandate</c>. Sent with <c>status: rejected</c>; the model never ran.
    /// </summary>
    BranchRefused,

    /// <summary>
    /// A <c>command</c> job's process exited non-zero (<c>command_failed</c>, spec 59 Q3). Sent with <c>status: failed</c>; the body has
    /// the exit code and the output's tail, the full output is attached.
    /// </summary>
    CommandFailed,

    /// <summary>
    /// A work machine's <b>mail job</b> — its <c>allowed_tools</c> explicitly name <c>zyggy outlook</c> — that also holds a rule beyond
    /// the outlook read verbs and <c>Read</c>/<c>Grep</c>/<c>Glob</c> (<c>work_mail_code_mix</c>, spec 58 decision B as corrected by the
    /// owner 2026-11-23). Sent with <c>status: rejected</c> at intake; the model never ran. Generic rules alone (<c>Bash(src/*)</c>,
    /// <c>Bash(dotnet *)</c>, bare <c>Bash</c>, <c>Edit</c>) never produce it.
    /// </summary>
    WorkMailCodeMix,
}
