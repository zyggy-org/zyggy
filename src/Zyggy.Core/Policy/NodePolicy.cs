namespace Zyggy.Core.Policy;

/// <summary>The run policy of one node (founding spec §6 Invocation and Concurrency, §10 <c>node.json</c> <c>claude.*</c>, <c>devRoots</c>, <c>maxConcurrentJobs</c>, <c>workHours</c>).</summary>
/// <param name="DefaultAllowedTools">The tool approval rules when a job names none (<c>claude.defaultAllowedTools</c>).</param>
/// <param name="DefaultTimeoutMinutes">The time limit when a job names none (<c>claude.defaultTimeoutMinutes</c>).</param>
/// <param name="MaxTurns">The agentic turn limit of every run (<c>claude.maxTurns</c>).</param>
/// <param name="MaxBudgetUsd">The per-run spending cap (<c>claude.maxBudgetUsd</c>); <see langword="null"/> sets none.</param>
/// <param name="DevRoots">The project roots (<c>devRoots</c>); read, not enforced until the registry (O18).</param>
/// <param name="MaxConcurrentJobs">How many jobs may run at once on this machine (<c>maxConcurrentJobs</c>, 2).</param>
/// <param name="WorkHours">The working window (<c>workHours</c>); <see langword="null"/> means always inside.</param>
public sealed record NodePolicy(
    IReadOnlyList<string> DefaultAllowedTools,
    int DefaultTimeoutMinutes,
    int MaxTurns,
    decimal? MaxBudgetUsd,
    IReadOnlyList<string> DevRoots,
    int MaxConcurrentJobs,
    WorkHours? WorkHours)
{
    /// <summary>Gets the §10 defaults: <c>Read, Grep, Glob</c>, 30 minutes, 60 turns, no budget cap, no roots, 2 jobs, no window.</summary>
    public static NodePolicy Default { get; } = new(["Read", "Grep", "Glob"], 30, 60, null, [], 2, null);
}
