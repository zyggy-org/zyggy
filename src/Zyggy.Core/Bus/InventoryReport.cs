using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Bus;

/// <summary>One machine's folder counts (spec 08 AC-5): <c>*.md</c> files, not parsed.</summary>
/// <param name="Machine">The machine.</param>
/// <param name="Queued">Files in <c>jobs/</c> (top level).</param>
/// <param name="Claimed">Files in <c>jobs/claimed/</c>.</param>
/// <param name="Rejected">Files in <c>jobs/rejected/</c>.</param>
/// <param name="Reports">Files in <c>reports/</c>.</param>
/// <param name="Context">Files in <c>context/</c>.</param>
public sealed record MachineCounts(MachineName Machine, int Queued, int Claimed, int Rejected, int Reports, int Context);

/// <summary>One envelope as the inventory sees it: verified, or read leniently with its problem named.</summary>
/// <param name="RelativePath">Its path in the checkout.</param>
/// <param name="Id">Its id (from the file name).</param>
/// <param name="Type">The <c>type</c>, when readable.</param>
/// <param name="From">The <c>from</c>, when readable.</param>
/// <param name="To">The <c>to</c>, when readable.</param>
/// <param name="Created">The <c>created</c>, when readable.</param>
/// <param name="InReplyTo">The <c>in_reply_to</c>, when readable.</param>
/// <param name="Status">A report's <c>status</c>, when readable.</param>
/// <param name="Reason">A report's <c>reason</c>, when present.</param>
/// <param name="CostUsd">A report's <c>cost_usd</c>, when readable.</param>
/// <param name="Verified"><see langword="true"/> when the file parsed and its signature verified.</param>
/// <param name="Problem">The §4 rejection reason's wire name when not verified; <see langword="null"/> otherwise.</param>
/// <param name="Project">A verified job's <c>project</c> (the trace line prints it).</param>
/// <param name="Priority">A verified envelope's <c>priority</c>.</param>
public sealed record EnvelopeSummary(
    string RelativePath,
    EnvelopeId Id,
    EnvelopeType? Type,
    MachineName? From,
    MachineName? To,
    DateTimeOffset? Created,
    EnvelopeId? InReplyTo,
    ReportStatus? Status,
    string? Reason,
    decimal? CostUsd,
    bool Verified,
    string? Problem,
    string? Project = null,
    EnvelopePriority? Priority = null);

/// <summary>Where a job stands (spec 08 AC-5): the §4 state machine read back from the files.</summary>
public enum TraceState
{
    /// <summary>In a machine's <c>jobs/</c>.</summary>
    Queued,

    /// <summary>In a machine's <c>jobs/claimed/</c>.</summary>
    Claimed,

    /// <summary>In <c>jobs/rejected/</c>, or answered by a verified <c>status: rejected</c> report.</summary>
    Rejected,

    /// <summary>Answered by a verified report that is not a rejection.</summary>
    Reported,

    /// <summary>Nowhere (the trace is <see langword="null"/> then).</summary>
    Unknown,
}

/// <summary>One job traced through the bus.</summary>
/// <param name="Id">The job id.</param>
/// <param name="State">Where it stands.</param>
/// <param name="Machine">The machine whose folders hold it, or the newest report's sender when only reports remain.</param>
/// <param name="Job">The job file, when one is still on the bus.</param>
/// <param name="Reports">Every report answering it, oldest first, including the archive.</param>
/// <param name="ReportBody">The body of the newest verified report; never of an unverified one.</param>
public sealed record EnvelopeTrace(EnvelopeId Id, TraceState State, MachineName? Machine, EnvelopeSummary? Job, IReadOnlyList<EnvelopeSummary> Reports, string? ReportBody);

/// <summary>The tenant's bus at a glance.</summary>
/// <param name="HeadSha">The checkout's <c>HEAD</c> at the time of the read.</param>
/// <param name="Machines">Every machine under <c>nodes/</c>, ordinal order.</param>
/// <param name="RecentReports">The newest reports across all machines, newest first.</param>
/// <param name="TenantDirectories">The directory names under <c>tenants/</c>, ordinal order (plan 08 A12: one per tenant is the rule; the auditor reports a second one).</param>
public sealed record InventoryReport(string? HeadSha, IReadOnlyList<MachineCounts> Machines, IReadOnlyList<EnvelopeSummary> RecentReports, IReadOnlyList<string> TenantDirectories);
