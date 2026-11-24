using Zyggy.Core.Envelope;
using Zyggy.Core.Runs;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Bus;

/// <summary>Where a mandate stands (deliverable 18, "The mandate"). When several apply, the first in this order is the state.</summary>
public enum MandateState
{
    /// <summary>No file with that id is in <c>mandates/</c> or <c>mandates/revoked/</c>.</summary>
    Absent,

    /// <summary>The file is there but does not verify as a mandate of Central under its own id.</summary>
    Unverified,

    /// <summary>The file is in <c>mandates/revoked/</c>.</summary>
    Revoked,

    /// <summary><c>expires</c> has passed.</summary>
    Expired,

    /// <summary>Its three newest reports since the last resume all have a status other than <c>done</c>.</summary>
    Paused,

    /// <summary>Its counted jobs have reached <c>max_jobs</c>.</summary>
    Exhausted,

    /// <summary>Jobs may be submitted and run under it.</summary>
    Active,
}

/// <summary>The limit of a mandate a submit or a job broke (deliverable 18): a closed list, each with its wire string (<see cref="MandateRules.ToWire"/>).</summary>
public enum MandateLimit
{
    /// <summary>The mandate does not exist.</summary>
    Absent,

    /// <summary>The mandate file does not verify, or is not from Central.</summary>
    Unverified,

    /// <summary>The mandate is revoked.</summary>
    Revoked,

    /// <summary>The mandate is expired.</summary>
    Expired,

    /// <summary>The mandate is paused after three reports in a row that are not <c>done</c>.</summary>
    Paused,

    /// <summary>The total of counted jobs has reached <c>max_jobs</c>.</summary>
    TotalCap,

    /// <summary>The job goes to another machine than the mandate names.</summary>
    Machine,

    /// <summary>The job names another project than the mandate.</summary>
    Project,

    /// <summary>A tool of the job is not an entry of the mandate's <c>allowed_tools</c>, or the job names no tools.</summary>
    Tool,

    /// <summary>The job's <c>base</c> is not one of the mandate's <c>branches</c>.</summary>
    Base,

    /// <summary>The jobs counted in the last 60 minutes have reached <c>max_jobs_per_hour</c>.</summary>
    HourlyCap,

    /// <summary>The jobs counted for that step have reached <c>max_jobs_per_step</c>.</summary>
    StepCap,

    /// <summary>The kind of job needs a grant the mandate does not carry.</summary>
    Grant,

    /// <summary>A session task under a mandate without <c>sessions: true</c>.</summary>
    Sessions,

    /// <summary>The newest report under the mandate is a usage limit less than 60 minutes old.</summary>
    UsageLimit,

    /// <summary>The target is a work machine and the job is not a session task.</summary>
    WorkMachine,

    /// <summary>The target's registry file does not list the capability <c>supervision</c>.</summary>
    Capability,

    /// <summary>The job has neither <c>worktree: true</c> nor <c>deliver: session</c>.</summary>
    Worktree,

    /// <summary>
    /// The job's <c>continue</c> names a job that did not run under this mandate and the same step (spec 59 slice 6 tightening,
    /// Central 2026-11-21): a mandate job continues only a branch of its own mandate's step.
    /// </summary>
    Continue,
}

/// <summary>A job under a mandate that sits in a machine's <c>jobs/</c> or <c>jobs/claimed/</c>, or was reported.</summary>
/// <param name="Id">The job's id.</param>
/// <param name="Created">When it was created: its <c>created</c> while the file exists, the time in its id once only its report is left.</param>
/// <param name="Step">Its step.</param>
public sealed record MandateJobEntry(EnvelopeId Id, DateTimeOffset Created, string? Step);

/// <summary>A verified report that carries the mandate's id.</summary>
/// <param name="Id">The report's id.</param>
/// <param name="Job">The job it answers.</param>
/// <param name="Created">When it was written.</param>
/// <param name="Status">Its status.</param>
/// <param name="Reason">Its reason, when not <c>done</c>.</param>
/// <param name="Step">The step copied from its job.</param>
/// <param name="UsageLimit"><see langword="true"/> when it is a <c>failed</c> / <c>claude_error</c> report whose body starts with <c>claude_error: rate_limit</c>.</param>
/// <param name="ModelStarted">
/// <see langword="true"/> when the report carries <c>model</c> or <c>num_turns</c> (spec 59, Q5): the model ran. A report with neither
/// is a failure before the model started (a missing base, a refused branch, an unknown project) and does not count toward the pause.
/// </param>
public sealed record MandateReportEntry(EnvelopeId Id, EnvelopeId Job, DateTimeOffset Created, ReportStatus Status, string? Reason, string? Step, bool UsageLimit, bool ModelStarted);

/// <summary>
/// Everything a bus checkout says about one mandate (deliverable 18): the file, the open jobs and the reports that carry its id, and
/// the owner's newest resume. Built by <see cref="BusInventory.MandateLedgerAsync"/>; the rules that read it are pure.
/// </summary>
/// <param name="Id">The mandate's id.</param>
/// <param name="Mandate">The mandate when its file verifies as a mandate of Central under that id; otherwise <see langword="null"/>.</param>
/// <param name="FilePresent"><see langword="true"/> when a file with that id is in <c>mandates/</c> or <c>mandates/revoked/</c>.</param>
/// <param name="Revoked"><see langword="true"/> when the file is in <c>mandates/revoked/</c>.</param>
/// <param name="OpenJobs">The verified jobs carrying the id in a <c>jobs/</c> or <c>jobs/claimed/</c> folder, oldest first.</param>
/// <param name="Reports">The verified reports carrying the id, live and archived, oldest first.</param>
/// <param name="Resumed">When the newest <c>resume</c> decision on the mandate was written; <see langword="null"/> when there is none.</param>
public sealed record MandateLedger(
    EnvelopeId Id,
    Envelope.Envelope? Mandate,
    bool FilePresent,
    bool Revoked,
    IReadOnlyList<MandateJobEntry> OpenJobs,
    IReadOnlyList<MandateReportEntry> Reports,
    DateTimeOffset? Resumed)
{
    private const int PauseAfter = 3;

    /// <summary>
    /// Gets the jobs that count (the one rule of both ends): a job in <c>jobs/</c> or <c>jobs/claimed/</c>, or a job with a report
    /// that is not <c>rejected</c>. Each job once, oldest first.
    /// </summary>
    public IReadOnlyList<MandateJobEntry> Counted { get; } =
    [
        .. OpenJobs
            .Concat(Reports.Where(r => r.Status != ReportStatus.Rejected).Select(r => new MandateJobEntry(r.Job, r.Job.Timestamp, r.Step)))
            .GroupBy(j => j.Id)
            .Select(g => g.First())
            .OrderBy(j => j.Id.Value, StringComparer.Ordinal),
    ];

    /// <summary>
    /// Gets a value indicating whether the three newest reports of runs where the model started, since the last resume, all have a
    /// status other than <c>done</c>. A report without <c>model</c> and <c>num_turns</c> (spec 59, Q5) is a failure before the model
    /// started and does not count, nor does a <c>withdrawn</c> report (deliverable 61, Q4 (a)): the owner stopped that job, it did
    /// not fail.
    /// </summary>
    public bool Paused
    {
        get
        {
            MandateReportEntry[] newest = [.. Reports.Where(r => r.ModelStarted && !IsWithdrawn(r) && (Resumed is null || r.Created > Resumed)).OrderByDescending(r => r.Id.Value, StringComparer.Ordinal).Take(PauseAfter)];
            return newest.Length == PauseAfter && newest.All(r => r.Status != ReportStatus.Done);
        }
    }

    private static bool IsWithdrawn(MandateReportEntry report) =>
        string.Equals(report.Reason, RunFailureReasonWire.ToWire(RunFailureReason.Withdrawn), StringComparison.Ordinal);

    /// <summary>The mandate's state at <paramref name="now"/>.</summary>
    /// <param name="now">The instant to judge at.</param>
    /// <returns>The state; the first that applies in the order of <see cref="MandateState"/>.</returns>
    public MandateState StateAt(DateTimeOffset now) =>
        !FilePresent ? MandateState.Absent
        : Mandate?.Mandate is not { } fields ? MandateState.Unverified
        : Revoked ? MandateState.Revoked
        : now >= fields.Expires ? MandateState.Expired
        : Paused ? MandateState.Paused
        : Counted.Count >= fields.MaxJobs ? MandateState.Exhausted
        : MandateState.Active;
}

/// <summary>What Central is about to submit under a mandate (deliverable 18, "Enforced on Central").</summary>
/// <param name="To">The target machine.</param>
/// <param name="Project">The project on that machine.</param>
/// <param name="Kind">The kind of job.</param>
/// <param name="Step">The step label.</param>
/// <param name="Tools">The tools the job will carry.</param>
/// <param name="Base">The branch the job starts from, when it names one.</param>
/// <param name="Session"><see langword="true"/> for a task delivered into a live session.</param>
/// <param name="TargetIsWorkMachine"><see langword="true"/> when the target is listed in <c>supervision.workMachines</c>.</param>
/// <param name="TargetHasCapability"><see langword="true"/> when the target's registry file lists the capability <c>supervision</c>.</param>
/// <param name="Continue">The earlier job whose branch the run starts from (spec 59 slice 6), when the job names one.</param>
public sealed record MandateSubmit(
    MachineName To,
    string Project,
    MandateJobKind Kind,
    string Step,
    IReadOnlyList<string> Tools,
    string? Base = null,
    bool Session = false,
    bool TargetIsWorkMachine = false,
    bool TargetHasCapability = true,
    EnvelopeId? Continue = null);

/// <summary>The answer of a mandate check: allowed, or refused naming one limit.</summary>
/// <param name="Limit">The limit that refuses; <see langword="null"/> when allowed.</param>
/// <param name="Detail">One line saying what broke the limit; <see langword="null"/> when allowed.</param>
public sealed record MandateVerdict(MandateLimit? Limit, string? Detail)
{
    /// <summary>Gets the verdict of a request inside every limit.</summary>
    public static MandateVerdict Ok { get; } = new(null, null);

    /// <summary>Gets a value indicating whether the request is inside every limit.</summary>
    public bool Allowed => Limit is null;
}

/// <summary>
/// The one check both ends run on a mandate (deliverable 18): Central before it submits a job, a node before it runs one. Pure: it
/// reads a <see cref="MandateLedger"/> and the instant given, never the clock, git or the network.
/// </summary>
public static class MandateRules
{
    private static readonly TimeSpan Hour = TimeSpan.FromMinutes(60);

    private static readonly TimeSpan UsageLimitBackOff = TimeSpan.FromMinutes(60);

    /// <summary>Returns the wire string of a limit: what a refusal line names.</summary>
    /// <param name="limit">The member.</param>
    /// <returns>A snake_case token.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for an undefined member.</exception>
    public static string ToWire(MandateLimit limit) => limit switch
    {
        MandateLimit.Absent => "absent",
        MandateLimit.Unverified => "unverified",
        MandateLimit.Revoked => "revoked",
        MandateLimit.Expired => "expired",
        MandateLimit.Paused => "paused",
        MandateLimit.TotalCap => "max_jobs",
        MandateLimit.Machine => "machine",
        MandateLimit.Project => "project",
        MandateLimit.Tool => "allowed_tools",
        MandateLimit.Base => "branches",
        MandateLimit.HourlyCap => "max_jobs_per_hour",
        MandateLimit.StepCap => "max_jobs_per_step",
        MandateLimit.Grant => "grants",
        MandateLimit.Sessions => "sessions",
        MandateLimit.UsageLimit => "usage_limit",
        MandateLimit.WorkMachine => "work_machine",
        MandateLimit.Capability => "capability",
        MandateLimit.Worktree => "worktree",
        MandateLimit.Continue => "continue",
        _ => throw new ArgumentOutOfRangeException(nameof(limit)),
    };

    /// <summary>Decides whether Central may submit <paramref name="submit"/> under the mandate of <paramref name="ledger"/>.</summary>
    /// <param name="ledger">What the bus checkout says about the mandate.</param>
    /// <param name="submit">The job about to be submitted.</param>
    /// <param name="now">The instant to judge at.</param>
    /// <returns><see cref="MandateVerdict.Ok"/>, or the first limit broken.</returns>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    public static MandateVerdict CheckSubmit(MandateLedger ledger, MandateSubmit submit, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(submit);
        if (Standing(ledger, now, pauseAndCap: true) is { } standing)
        {
            return standing;
        }

        Envelope.Envelope mandate = ledger.Mandate!;
        MandateFields fields = mandate.Mandate!;
        if (Scope(ledger, submit.To, submit.Project, submit.Tools, submit.Base, submit.Step, submit.Continue, self: null) is { } scope)
        {
            return scope;
        }

        if (ledger.Counted.Count(j => now - j.Created < Hour) >= fields.MaxJobsPerHour)
        {
            return Refused(MandateLimit.HourlyCap, "the mandate allows " + fields.MaxJobsPerHour + " job(s) an hour");
        }

        if (ledger.Counted.Count(j => j.Step == submit.Step) >= fields.MaxJobsPerStep)
        {
            return Refused(MandateLimit.StepCap, "step '" + submit.Step + "' has had its " + fields.MaxJobsPerStep + " job(s)");
        }

        if (GrantFor(submit.Kind) is { } needed && !fields.Grants.Contains(needed))
        {
            return Refused(MandateLimit.Grant, "a " + EnvelopeWire.ToWire(submit.Kind) + " job needs the grant " + EnvelopeWire.ToWire(needed));
        }

        if (submit.Session && !fields.Sessions)
        {
            return Refused(MandateLimit.Sessions, "the mandate does not allow tasks into a live session");
        }

        if (ledger.Reports.Count > 0 && ledger.Reports[^1] is { UsageLimit: true } last && now - last.Created < UsageLimitBackOff)
        {
            return Refused(MandateLimit.UsageLimit, "the newest report is a usage limit of less than 60 minutes ago");
        }

        if (submit.TargetIsWorkMachine && !submit.Session)
        {
            return Refused(MandateLimit.WorkMachine, submit.To.Value + " is a work machine: only a session task may go there");
        }

        return submit.TargetHasCapability
            ? MandateVerdict.Ok
            : Refused(MandateLimit.Capability, submit.To.Value + " does not publish the capability supervision: upgrade its node first");
    }

    /// <summary>Decides whether the node <paramref name="me"/> may run <paramref name="job"/> under the mandate of <paramref name="ledger"/>.</summary>
    /// <param name="ledger">What this node's bus checkout says about the mandate the job names.</param>
    /// <param name="job">The verified job.</param>
    /// <param name="me">This node's machine.</param>
    /// <param name="now">The instant to judge at.</param>
    /// <returns><see cref="MandateVerdict.Ok"/>, or the first limit broken.</returns>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="job"/> is not a job.</exception>
    public static MandateVerdict CheckIntake(MandateLedger ledger, Envelope.Envelope job, MachineName me, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(me);
        JobFields fields = job.Job ?? throw new ArgumentException("The envelope is not a job.", nameof(job));
        if (Standing(ledger, now, pauseAndCap: false) is { } standing)
        {
            return standing;
        }

        if (Scope(ledger, me, fields.Project, fields.AllowedTools ?? [], fields.Base, fields.Step, fields.Continue, job.Header.Id) is { } scope)
        {
            return scope;
        }

        int max = ledger.Mandate!.Mandate!.MaxJobs;
        if (ledger.Counted.Count(j => j.Id != job.Header.Id) >= max)
        {
            return Refused(MandateLimit.TotalCap, "the mandate's " + max + " job(s) are used");
        }

        return fields.Worktree || fields.Deliver == JobDelivery.Session
            ? MandateVerdict.Ok
            : Refused(MandateLimit.Worktree, "a job under a mandate runs in a worktree or is a session task");
    }

    // What the mandate itself says, before the request is looked at. Pause and the total cap are Central's to refuse a submit on;
    // a node counts the other jobs itself and leaves the pause to Central (spec 18, "Enforced on the node").
    private static MandateVerdict? Standing(MandateLedger ledger, DateTimeOffset now, bool pauseAndCap) => ledger.StateAt(now) switch
    {
        MandateState.Absent => Refused(MandateLimit.Absent, "no mandate " + ledger.Id.Value + " on the bus"),
        MandateState.Unverified => Refused(MandateLimit.Unverified, "the mandate file does not verify"),
        MandateState.Revoked => Refused(MandateLimit.Revoked, "the mandate is revoked"),
        MandateState.Expired => Refused(MandateLimit.Expired, "the mandate expired"),
        MandateState.Paused when pauseAndCap => Refused(MandateLimit.Paused, "the mandate is paused after three reports in a row that are not done"),
        MandateState.Exhausted when pauseAndCap => Refused(MandateLimit.TotalCap, "the mandate's " + ledger.Mandate!.Mandate!.MaxJobs + " job(s) are used"),
        _ => null,
    };

    // The mandate's scope: machine, project, tools, base, and (spec 59 slice 6 tightening, Central 2026-11-21) the job a continue names,
    // which must be a counted job of this mandate with the same step — never the job itself, another step's, another mandate's or none's.
    private static MandateVerdict? Scope(
        MandateLedger ledger,
        MachineName machine,
        string project,
        IReadOnlyList<string> tools,
        string? baseBranch,
        string? step,
        EnvelopeId? continueFrom,
        EnvelopeId? self)
    {
        Envelope.Envelope mandate = ledger.Mandate!;
        MandateFields fields = mandate.Mandate!;
        if (mandate.Header.To != machine)
        {
            return Refused(MandateLimit.Machine, "the mandate is for " + mandate.Header.To.Value);
        }

        if (!string.Equals(fields.Project, project, StringComparison.Ordinal))
        {
            return Refused(MandateLimit.Project, "the mandate is for project " + fields.Project);
        }

        if (tools.Count == 0)
        {
            return Refused(MandateLimit.Tool, "a job under a mandate names its tools");
        }

        if (tools.FirstOrDefault(t => !fields.AllowedTools.Contains(t, StringComparer.Ordinal)) is { } outside)
        {
            return Refused(MandateLimit.Tool, "'" + outside + "' is not in the mandate's allowed_tools");
        }

        if (baseBranch is not null && !fields.Branches.Contains(baseBranch, StringComparer.Ordinal))
        {
            return Refused(MandateLimit.Base, "'" + baseBranch + "' is not one of the mandate's branches");
        }

        return continueFrom is null
            || (continueFrom != self && ledger.Counted.Any(j => j.Id == continueFrom && string.Equals(j.Step, step, StringComparison.Ordinal)))
            ? null
            : Refused(MandateLimit.Continue, "continue: " + continueFrom.Value + " is not a job of this mandate's step '" + step + "'");
    }

    private static MandateGrant? GrantFor(MandateJobKind kind) => kind switch
    {
        MandateJobKind.Merge => MandateGrant.Merge,
        MandateJobKind.Release => MandateGrant.Release,
        MandateJobKind.Deploy => MandateGrant.Deploy,
        _ => null,
    };

    private static MandateVerdict Refused(MandateLimit limit, string detail) => new(limit, detail);
}
