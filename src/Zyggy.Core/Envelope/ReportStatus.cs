namespace Zyggy.Core.Envelope;

/// <summary>The report <c>status</c> field (founding spec §4); a <c>reason</c> is required whenever it is not <see cref="Done"/>.</summary>
public enum ReportStatus
{
    /// <summary><c>done</c>.</summary>
    Done,

    /// <summary><c>failed</c>.</summary>
    Failed,

    /// <summary><c>timeout</c>.</summary>
    Timeout,

    /// <summary><c>rejected</c>: the job never ran.</summary>
    Rejected,
}
