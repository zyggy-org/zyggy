namespace Zyggy.Core.Registry;

/// <summary>What one registry publish did (spec 14 Contracts).</summary>
public enum PublishOutcome
{
    /// <summary>A commit was pushed.</summary>
    Published,

    /// <summary>The content was unchanged and the liveness stamp fresh: no commit.</summary>
    Unchanged,

    /// <summary>Another zyggy process held the bus checkout's write lock.</summary>
    Busy,

    /// <summary>The write failed or the push kept failing; the commit may stay local.</summary>
    Failed,
}

/// <summary>A publish outcome with the redacted detail <c>zyggy discover</c> prints (plan 14 A4).</summary>
/// <param name="Outcome">The outcome.</param>
/// <param name="Detail">The redacted failure detail, or <see langword="null"/>.</param>
public sealed record RegistryPublishResult(PublishOutcome Outcome, string? Detail);
