using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Bus;

/// <summary>How a live session looks from its status, judged by the reader's clock (deliverable 18, "Reading").</summary>
public enum SessionActivity
{
    /// <summary>The last turn ended less than 10 minutes ago.</summary>
    Working,

    /// <summary>The last turn ended 10 minutes ago or more.</summary>
    Idle,

    /// <summary>The status file itself is 60 minutes old or more: the session stopped saying anything.</summary>
    Silent,
}

/// <summary>One session of a machine, from a status that verified and passed the writer rule.</summary>
/// <param name="Activity">Working, idle or silent.</param>
/// <param name="Status">The status fields; for a work machine only the four fixed ones.</param>
public sealed record WorkSession(SessionActivity Activity, StatusFields Status);

/// <summary>One job of a machine that has no report yet.</summary>
/// <param name="Id">The job id.</param>
/// <param name="Project">The job's project when the file verifies; otherwise <see langword="null"/>.</param>
/// <param name="Claimed"><see langword="true"/> in <c>jobs/claimed/</c>, <see langword="false"/> in <c>jobs/</c>.</param>
/// <param name="Submitted">When the job was created (the time in its id).</param>
public sealed record WorkJob(EnvelopeId Id, string? Project, bool Claimed, DateTimeOffset Submitted);

/// <summary>What one machine is doing: its live sessions, its open jobs and its newest report.</summary>
/// <param name="Machine">The machine.</param>
/// <param name="Sessions">Its sessions, newest activity first.</param>
/// <param name="Jobs">Its claimed, then queued jobs, oldest first.</param>
/// <param name="LastReport">The newest file in its <c>reports/</c> folder, when there is one.</param>
public sealed record MachineWork(MachineName Machine, IReadOnlyList<WorkSession> Sessions, IReadOnlyList<WorkJob> Jobs, EnvelopeSummary? LastReport);

/// <summary>The board <c>zyggy work</c> prints (deliverable 18).</summary>
/// <param name="At">The reader's clock.</param>
/// <param name="Machines">The machines that have a session, an open job or a report, in name order.</param>
/// <param name="StatusesNotShown">Status files left out because their signature or the writer rule failed.</param>
public sealed record WorkBoard(DateTimeOffset At, IReadOnlyList<MachineWork> Machines, int StatusesNotShown);
