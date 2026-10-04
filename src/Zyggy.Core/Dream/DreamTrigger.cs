namespace Zyggy.Core.Dream;

/// <summary>What started a dream run.</summary>
public enum DreamTrigger
{
    /// <summary>The 03:00 timer.</summary>
    Nightly,

    /// <summary>The owner's request (the request file and the path unit).</summary>
    OnDemand,

    /// <summary>A run by hand.</summary>
    Manual,
}

/// <summary>What a dream run ended with.</summary>
public enum DreamRunOutcome
{
    /// <summary>Accepted work was committed (also when a cap stopped the batch loop).</summary>
    Committed,

    /// <summary>No unconsumed line and nothing to compress.</summary>
    NothingToDo,

    /// <summary>A check refused the run's first batch, or the run-level breaker refused the run.</summary>
    Aborted,

    /// <summary>The model, the lock or git failed before anything was committed.</summary>
    Failed,

    /// <summary>Some batches were committed; a later one was aborted or failed.</summary>
    Partial,
}

/// <summary>The wire strings of <see cref="DreamTrigger"/>.</summary>
public static class DreamTriggerWire
{
    /// <summary>Returns the wire string of a trigger.</summary>
    /// <param name="trigger">The trigger.</param>
    /// <returns><c>nightly</c>, <c>on-demand</c> or <c>manual</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for an undefined member.</exception>
    public static string ToWire(DreamTrigger trigger) => trigger switch
    {
        DreamTrigger.Nightly => "nightly",
        DreamTrigger.OnDemand => "on-demand",
        DreamTrigger.Manual => "manual",
        _ => throw new ArgumentOutOfRangeException(nameof(trigger)),
    };

    /// <summary>Maps a wire string to a trigger.</summary>
    /// <param name="wire">The wire string.</param>
    /// <param name="trigger">The trigger when recognised.</param>
    /// <returns><see langword="true"/> when <paramref name="wire"/> is an exact wire string.</returns>
    public static bool TryFromWire(string? wire, out DreamTrigger trigger)
    {
        foreach (var member in Enum.GetValues<DreamTrigger>())
        {
            if (string.Equals(ToWire(member), wire, StringComparison.Ordinal))
            {
                trigger = member;
                return true;
            }
        }

        trigger = default;
        return false;
    }
}

/// <summary>The wire strings of <see cref="DreamRunOutcome"/>.</summary>
public static class DreamRunOutcomeWire
{
    /// <summary>Returns the wire string of an outcome.</summary>
    /// <param name="outcome">The outcome.</param>
    /// <returns>The snake_case outcome, for example <c>nothing_to_do</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for an undefined member.</exception>
    public static string ToWire(DreamRunOutcome outcome) => outcome switch
    {
        DreamRunOutcome.Committed => "committed",
        DreamRunOutcome.NothingToDo => "nothing_to_do",
        DreamRunOutcome.Aborted => "aborted",
        DreamRunOutcome.Failed => "failed",
        DreamRunOutcome.Partial => "partial",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };
}
