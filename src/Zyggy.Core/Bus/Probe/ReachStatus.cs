namespace Zyggy.Core.Bus.Probe;

/// <summary>The result of one <c>zyggy probe reach</c> check (deliverable 04, the §1 reachability matrix). Closed.</summary>
public enum ReachStatus
{
    /// <summary>The endpoint answered and accepted us.</summary>
    Ok,

    /// <summary>The endpoint was reached but refused our credential (or we had none).</summary>
    AuthFailed,

    /// <summary>The endpoint could not be reached: DNS, connection, proxy refusal, timeout.</summary>
    Unreachable,

    /// <summary>The connection was made but TLS failed (an inspecting proxy, an untrusted root).</summary>
    TlsFailed,

    /// <summary>The check was not run (for example no SSH remote is configured).</summary>
    Skipped,
}

/// <summary>The wire strings of <see cref="ReachStatus"/> as printed in the matrix.</summary>
public static class ReachStatusWire
{
    /// <summary>Returns the printed form of a status.</summary>
    /// <param name="value">The member.</param>
    /// <returns><c>ok</c>, <c>auth failed</c>, <c>unreachable</c>, <c>tls failed</c> or <c>skipped</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for an undefined member.</exception>
    public static string ToWire(ReachStatus value) => value switch
    {
        ReachStatus.Ok => "ok",
        ReachStatus.AuthFailed => "auth failed",
        ReachStatus.Unreachable => "unreachable",
        ReachStatus.TlsFailed => "tls failed",
        ReachStatus.Skipped => "skipped",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };
}
