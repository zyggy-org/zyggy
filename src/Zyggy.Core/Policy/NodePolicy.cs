namespace Zyggy.Core.Policy;

/// <summary>The run policy of one node (founding spec §6 Invocation and Concurrency, §10 <c>node.json</c> <c>claude.*</c>, <c>devRoots</c>, <c>maxConcurrentJobs</c>, <c>workHours</c>).</summary>
/// <param name="DefaultAllowedTools">The tool approval rules when a job names none (<c>claude.defaultAllowedTools</c>).</param>
/// <param name="DefaultTimeoutMinutes">The time limit when a job names none (<c>claude.defaultTimeoutMinutes</c>).</param>
/// <param name="MaxTurns">The agentic turn limit of every run (<c>claude.maxTurns</c>).</param>
/// <param name="MaxBudgetUsd">The per-run spending cap (<c>claude.maxBudgetUsd</c>); <see langword="null"/> sets none.</param>
/// <param name="DevRoots">The project roots (<c>devRoots</c>): the scan's roots, and the only folders a job path may name (§6 step 1, A14-3).</param>
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
    /// <summary>The default of <see cref="FetchMaxBytes"/>: 1 MiB (spec 59, Q2).</summary>
    public const int DefaultFetchMaxBytes = 1_048_576;

    /// <summary>Gets the §10 defaults: <c>Read, Grep, Glob</c>, 30 minutes, 60 turns, no budget cap, no roots, 2 jobs, no window.</summary>
    public static NodePolicy Default { get; } = new(["Read", "Grep", "Glob"], 30, 60, null, [], 2, null);

    /// <summary>
    /// Gets the largest file a <c>fetch</c> job reads, and the cap of any file attached to a report (<c>jobs.fetchMaxBytes</c>, spec 59);
    /// <see cref="DefaultFetchMaxBytes"/> when absent.
    /// </summary>
    public int FetchMaxBytes { get; init; } = DefaultFetchMaxBytes;

    /// <summary>
    /// Gets the allow-list of <c>command</c> jobs (<c>node.json</c> <c>commands</c>, spec 59 slice 2): name → argv; the only source of a
    /// command's program and arguments. Empty when absent.
    /// </summary>
    public IReadOnlyDictionary<string, NodeCommand> Commands { get; init; } = new Dictionary<string, NodeCommand>(StringComparer.Ordinal);

    /// <summary>
    /// Gets whether this node is a work machine that reads work mail (<c>node.json</c> <c>outlook.enabled</c>, spec 58 decision B): its
    /// mail jobs run with the outlook read verbs and file reads only, other jobs may not call <c>zyggy outlook</c>, and its job hook
    /// refuses every outlook action (as corrected by the owner 2026-11-23: no job is refused for its tools). False when absent.
    /// </summary>
    public bool WorkMail { get; init; }
}

/// <summary>One entry of <c>node.json</c> <c>commands</c> (spec 59 slice 2).</summary>
/// <param name="Argv">The program (a bare name on PATH or an absolute path the owner wrote) and its fixed arguments; run without a shell.</param>
/// <param name="TimeoutMinutes">The run's time limit, 1–30; <see langword="null"/> uses <see cref="NodePolicy.DefaultTimeoutMinutes"/> capped at 30.</param>
public sealed record NodeCommand(IReadOnlyList<string> Argv, int? TimeoutMinutes = null);
