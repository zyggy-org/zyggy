using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Bus;

/// <summary>What Central's sweep did with one stale claim (spec 17; founding spec §4 state machine).</summary>
public enum SweepActionKind
{
    /// <summary>Written back to the machine's <c>jobs/</c> with <c>attempt</c> raised and re-signed by Central.</summary>
    Requeued,

    /// <summary>Removed and answered by Central's <c>failed</c> / <c>attempts_exhausted</c> report.</summary>
    Exhausted,

    /// <summary>Did not verify: moved unchanged to the machine's <c>jobs/rejected/</c>.</summary>
    Rejected,
}

/// <summary>One stale claim the sweep settled, as <c>zyggy sweep</c> prints it (spec 17 "CLI").</summary>
/// <param name="Kind">What was done.</param>
/// <param name="Id">The claimed file's id.</param>
/// <param name="Machine">The machine whose <c>jobs/claimed/</c> held it.</param>
/// <param name="Attempt">The new attempt of a re-queued job; the attempts used of an exhausted one; the attempt as read (1 when unreadable) of a rejected file.</param>
/// <param name="Reason">The §4 rejection reason of a rejected file; otherwise <see langword="null"/>.</param>
public sealed record SweepAction(SweepActionKind Kind, EnvelopeId Id, MachineName Machine, int Attempt, string? Reason);

/// <summary>What one sweep pass on Central did (spec 17).</summary>
/// <param name="Actions">The claims settled, in the order they were handled.</param>
/// <param name="Failure">The first write of the pass that failed (its claim was left in place); <see langword="null"/> when none did.</param>
/// <param name="PushDeferred">Whether a commit of the pass stayed local because every push was refused (§5).</param>
public sealed record SweepResult(IReadOnlyList<SweepAction> Actions, WriteFailed? Failure, bool PushDeferred)
{
    /// <summary>Gets the number of claims re-queued.</summary>
    public int Requeued => Actions.Count(a => a.Kind == SweepActionKind.Requeued);

    /// <summary>Gets the number of claims ended with an <c>attempts_exhausted</c> report.</summary>
    public int Exhausted => Actions.Count(a => a.Kind == SweepActionKind.Exhausted);

    /// <summary>Gets the number of claims rejected.</summary>
    public int Rejected => Actions.Count(a => a.Kind == SweepActionKind.Rejected);

    /// <summary>Gets the number of <c>reports/</c> and <c>context/</c> files moved to <c>archive/</c> by the pass's archive commit.</summary>
    public int Archived { get; init; }

    /// <summary>Gets the oldest <c>created</c> (or, for a file whose <c>created</c> cannot be read, adding-commit time) among the archived files; <see langword="null"/> when none was archived.</summary>
    public DateTimeOffset? OldestArchived { get; init; }
}
