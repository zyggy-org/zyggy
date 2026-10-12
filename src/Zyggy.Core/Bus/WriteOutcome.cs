namespace Zyggy.Core.Bus;

/// <summary>The outcome of one bus write sequence (founding spec §5 "Git operations", as amended by deliverable 04). Closed.</summary>
public abstract record WriteOutcome
{
    private protected WriteOutcome()
    {
    }
}

/// <summary>The change is on the remote branch.</summary>
/// <param name="Attempts">How many pushes it took (1 when the first succeeded).</param>
public sealed record Pushed(int Attempts) : WriteOutcome;

/// <summary>Every push was refused: the commit stays local and unpushed with a clean working copy; the next write carries it (§5, ex-OQ9).</summary>
/// <param name="Attempts">How many pushes were tried (four).</param>
/// <param name="Detail">The last push's redacted detail.</param>
public sealed record Deferred(int Attempts, string Detail) : WriteOutcome;

/// <summary>The sequence failed before the push (pull, move, add, remove, sign or commit); the working copy was restored.</summary>
/// <param name="Stage">The failed stage: <c>pull</c>, <c>mv</c>, <c>add</c>, <c>rm</c>, <c>sign</c> or <c>commit</c>.</param>
/// <param name="Detail">A short, redacted detail.</param>
public sealed record WriteFailed(string Stage, string Detail) : WriteOutcome;
