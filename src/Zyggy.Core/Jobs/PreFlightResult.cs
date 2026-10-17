using Zyggy.Core.Runs;

namespace Zyggy.Core.Jobs;

/// <summary>The outcome of the §6 pre-flight: the resolved project, or the reason and the check that refused it.</summary>
internal abstract record PreFlightResult
{
    private PreFlightResult()
    {
    }

    /// <summary>The project passed every check.</summary>
    /// <param name="ProjectPath">The normalised absolute project path.</param>
    /// <param name="Agent">The verified subagent name, when the job names one.</param>
    public sealed record Ok(string ProjectPath, string? Agent) : PreFlightResult;

    /// <summary>A check failed; <paramref name="Check"/> is one of the fixed check words, never the path's contents.</summary>
    /// <param name="Reason"><see cref="RunFailureReason.UnknownProject"/> or <see cref="RunFailureReason.UnknownAgent"/>.</param>
    /// <param name="Check"><c>not absolute</c>, <c>missing</c>, <c>not a git repository</c>, <c>no .claude</c>, <c>unknown agent</c> or <c>unsafe agent name</c>.</param>
    public sealed record Rejected(RunFailureReason Reason, string Check) : PreFlightResult;
}
