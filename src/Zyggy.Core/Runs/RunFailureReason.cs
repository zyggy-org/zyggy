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
    /// A job's <c>model</c> did not run (<c>model_unavailable</c>, spec 50), never replaced by another: <c>rejected</c> when the node's
    /// <c>claude.models</c> does not list it (the model never started); <c>failed</c> when Claude Code started another model or refused it.
    /// </summary>
    ModelUnavailable,

    /// <summary>
    /// The job was withdrawn by its sender or by Central (<c>withdrawn</c>, deliverable 61). Always sent with <c>status: failed</c>: by
    /// the withdrawing machine for a queued job (the model never ran), by the node for a claimed one it stopped. Never counts toward a
    /// mandate's pause. A 0.30.0 reader accepts it as any snake_case reason token.
    /// </summary>
    Withdrawn,
}
