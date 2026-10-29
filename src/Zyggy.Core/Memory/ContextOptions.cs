namespace Zyggy.Core.Memory;

/// <summary>
/// The constants of the Hub's <c>get_context</c> (spec 11, D2/D3). No environment knob in v1: the budget is the caller's
/// <c>max_tokens</c>, and these values change with a release, as <see cref="DigestOptions"/> does.
/// </summary>
internal sealed record ContextOptions
{
    /// <summary>Gets the bytes counted per token: the budget is <c>max_tokens × BytesPerToken</c> UTF-8 bytes.</summary>
    public int BytesPerToken { get; init; } = 4;

    /// <summary>Gets the <c>max_tokens</c> used when the caller gives none.</summary>
    public int DefaultMaxTokens { get; init; } = 2000;

    /// <summary>Gets the smallest accepted <c>max_tokens</c>.</summary>
    public int MinTokens { get; init; } = 100;

    /// <summary>Gets the largest accepted <c>max_tokens</c>.</summary>
    public int MaxTokens { get; init; } = 8000;

    /// <summary>Gets how many days back <c>daily/YYYY-MM-DD.md</c> files are read, by file-name date.</summary>
    public int DailyWindowDays { get; init; } = 30;

    /// <summary>Gets the share of the budget, in percent, the daily block may take at most.</summary>
    public int DailyBudgetPercent { get; init; } = 20;

    /// <summary>Gets the longest accepted topic, in Unicode scalar values after normalisation.</summary>
    public int TopicMaxChars { get; init; } = 200;
}
