namespace Zyggy.Core.Bus;

/// <summary>What the §5 item 6 safety fetch found (spec 07).</summary>
public enum SafetyFetchOutcome
{
    /// <summary><c>origin/&lt;branch&gt;</c> equals <c>HEAD</c>: the doorbell was right.</summary>
    Unchanged,

    /// <summary>The branch moved while the doorbell said unchanged: the listener pulls and processes.</summary>
    Moved,

    /// <summary>The fetch or the comparison failed (network, a deleted remote, a busy checkout); logged once per streak.</summary>
    Failed,
}

/// <summary>The safety fetch's result.</summary>
/// <param name="Outcome">What was found.</param>
/// <param name="Detail">The remote head's short SHA when <see cref="SafetyFetchOutcome.Moved"/>; git's redacted error when <see cref="SafetyFetchOutcome.Failed"/>; empty otherwise.</param>
public sealed record SafetyFetchResult(SafetyFetchOutcome Outcome, string Detail);
