namespace Zyggy.Core.Bus.Probe;

/// <summary>How one transport probe ended, as seen by the sender (deliverable 04, O21: measured at the sender only). Closed.</summary>
public enum ProbeOutcome
{
    /// <summary><c>done</c>: a verified <c>done</c> report from the target was observed within the timeout.</summary>
    Done,

    /// <summary><c>rejected</c>: the target answered with a <c>status: rejected</c> report.</summary>
    Rejected,

    /// <summary><c>timeout</c>: no report was observed within the timeout.</summary>
    Timeout,

    /// <summary><c>unverified_report</c>: a report answering the probe was observed but did not verify (unknown key, bad signature); recorded at once, not at the deadline (decision 0001 F3).</summary>
    UnverifiedReport,

    /// <summary><c>push_failed</c>: the probe job itself could not be pushed to the bus.</summary>
    PushFailed,
}

/// <summary>The wire strings of <see cref="ProbeOutcome"/> as written in the ledger.</summary>
public static class ProbeOutcomeWire
{
    /// <summary>Returns the wire string of an outcome.</summary>
    /// <param name="value">The member.</param>
    /// <returns><c>done</c>, <c>rejected</c>, <c>timeout</c>, <c>unverified_report</c> or <c>push_failed</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for an undefined member.</exception>
    public static string ToWire(ProbeOutcome value) => value switch
    {
        ProbeOutcome.Done => "done",
        ProbeOutcome.Rejected => "rejected",
        ProbeOutcome.Timeout => "timeout",
        ProbeOutcome.UnverifiedReport => "unverified_report",
        ProbeOutcome.PushFailed => "push_failed",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    /// <summary>Maps a wire string to an outcome.</summary>
    /// <param name="wire">The wire string.</param>
    /// <param name="value">The member when recognised.</param>
    /// <returns><see langword="true"/> when <paramref name="wire"/> is an exact wire string.</returns>
    public static bool TryFromWire(string? wire, out ProbeOutcome value)
    {
        foreach (ProbeOutcome member in Enum.GetValues<ProbeOutcome>())
        {
            if (string.Equals(ToWire(member), wire, StringComparison.Ordinal))
            {
                value = member;
                return true;
            }
        }

        value = default;
        return false;
    }
}
