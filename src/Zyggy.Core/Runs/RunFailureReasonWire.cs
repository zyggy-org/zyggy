namespace Zyggy.Core.Runs;

/// <summary>The single mapping between <see cref="RunFailureReason"/> and its exact snake_case wire strings (founding spec §9).</summary>
public static class RunFailureReasonWire
{
    /// <summary>Returns the wire string of a failure reason.</summary>
    /// <param name="value">The member.</param>
    /// <returns>The snake_case reason, for example <c>claude_error</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for an undefined member.</exception>
    public static string ToWire(RunFailureReason value) => value switch
    {
        RunFailureReason.UnknownProject => "unknown_project",
        RunFailureReason.UnknownAgent => "unknown_agent",
        RunFailureReason.Locked => "locked",
        RunFailureReason.Timeout => "timeout",
        RunFailureReason.DlpFilter => "dlp_filter",
        RunFailureReason.ClaudeError => "claude_error",
        RunFailureReason.GitError => "git_error",
        RunFailureReason.SchemaUnsupported => "schema_unsupported",
        RunFailureReason.BudgetExceeded => "budget_exceeded",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    /// <summary>Maps a wire string to a failure reason.</summary>
    /// <param name="wire">The wire string.</param>
    /// <param name="value">The member when recognised.</param>
    /// <returns><see langword="true"/> when <paramref name="wire"/> is an exact wire string.</returns>
    public static bool TryFromWire(string? wire, out RunFailureReason value)
    {
        foreach (var member in Enum.GetValues<RunFailureReason>())
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
