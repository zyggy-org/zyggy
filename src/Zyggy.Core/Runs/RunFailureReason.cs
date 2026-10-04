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
}
