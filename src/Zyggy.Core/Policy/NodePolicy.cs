namespace Zyggy.Core.Policy;

/// <summary>The run policy of one node (founding spec §6 Invocation, §10 <c>node.json</c> <c>claude.*</c> and <c>devRoots</c>).</summary>
/// <param name="DefaultAllowedTools">The tool approval rules when a job names none (<c>claude.defaultAllowedTools</c>).</param>
/// <param name="DefaultTimeoutMinutes">The time limit when a job names none (<c>claude.defaultTimeoutMinutes</c>).</param>
/// <param name="MaxTurns">The agentic turn limit of every run (<c>claude.maxTurns</c>).</param>
/// <param name="MaxBudgetUsd">The per-run spending cap (<c>claude.maxBudgetUsd</c>); <see langword="null"/> sets none.</param>
/// <param name="DevRoots">The project roots (<c>devRoots</c>); read, not enforced until the registry (O18).</param>
public sealed record NodePolicy(
    IReadOnlyList<string> DefaultAllowedTools,
    int DefaultTimeoutMinutes,
    int MaxTurns,
    decimal? MaxBudgetUsd,
    IReadOnlyList<string> DevRoots)
{
    /// <summary>Gets the §10 defaults: <c>Read, Grep, Glob</c>, 30 minutes, 60 turns, no budget cap, no roots.</summary>
    public static NodePolicy Default { get; } = new(["Read", "Grep", "Glob"], 30, 60, null, []);
}
