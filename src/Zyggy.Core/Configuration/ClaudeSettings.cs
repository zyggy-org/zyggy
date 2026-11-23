namespace Zyggy.Core.Configuration;

/// <summary>The <c>claude</c> section of <c>node.json</c> (founding spec §10 as amended by A4; spec 06 Configuration).</summary>
/// <param name="Path">The <c>claude</c> executable (<c>claude.path</c>); <see langword="null"/> keeps the model runner's default.</param>
/// <param name="MaxTurns">The agentic turn limit of every run (<c>claude.maxTurns</c>, 60).</param>
/// <param name="DefaultTimeoutMinutes">The time limit when a job names none (<c>claude.defaultTimeoutMinutes</c>, 30).</param>
/// <param name="DefaultAllowedTools">The tool rules when a job names none (<c>claude.defaultAllowedTools</c>); <see langword="null"/> when absent.</param>
/// <param name="MaxBudgetUsd">The per-run spending cap (<c>claude.maxBudgetUsd</c>); <see langword="null"/> sets none.</param>
public sealed record ClaudeSettings(
    string? Path = null,
    int MaxTurns = 60,
    int DefaultTimeoutMinutes = 30,
    IReadOnlyList<string>? DefaultAllowedTools = null,
    decimal? MaxBudgetUsd = null)
{
    private static readonly IReadOnlyList<string> CodeDefaultAllowedTools = ["Read", "Grep", "Glob"];

    /// <summary>Gets <see cref="DefaultAllowedTools"/>, or the code default <c>Read, Grep, Glob</c> when absent.</summary>
    public IReadOnlyList<string> EffectiveAllowedTools => DefaultAllowedTools ?? CodeDefaultAllowedTools;

    /// <summary>
    /// Gets the models a job may name on this machine (<c>claude.models</c>, spec 50): full Claude Code ids, never aliases;
    /// <see langword="null"/> when absent (any id that passed the job field's shape rule).
    /// </summary>
    public IReadOnlyList<string>? Models { get; init; }
}
