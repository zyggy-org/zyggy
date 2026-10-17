using System.Text.RegularExpressions;

using Zyggy.Core.Envelope;
using Zyggy.Core.Git;
using Zyggy.Core.Runs;

namespace Zyggy.Core.Jobs;

/// <summary>
/// The §6 pre-flight in its P1 form (O18: a path, before the registry exists): absolute, exists, a git repository, has <c>.claude/</c>,
/// and the named agent exists under <c>.claude/agents/</c>. Read-only: nothing is created and nothing but <c>git rev-parse --git-dir</c> runs.
/// </summary>
internal static partial class PreFlight
{
    public static async Task<PreFlightResult> CheckAsync(JobFields job, GitClient git, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(git);

        // A registry-style name (calizr) is not fully qualified and ends here (plan 06 A7).
        if (!Path.IsPathFullyQualified(job.Project))
        {
            return new PreFlightResult.Rejected(RunFailureReason.UnknownProject, "not absolute");
        }

        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(job.Project));
        if (!Directory.Exists(path))
        {
            return new PreFlightResult.Rejected(RunFailureReason.UnknownProject, "missing");
        }

        // A worktree's .git is a file, so git decides, not a directory check.
        if (await git.RevParseAsync(path, "--git-dir", ct).ConfigureAwait(false) is null)
        {
            return new PreFlightResult.Rejected(RunFailureReason.UnknownProject, "not a git repository");
        }

        var claude = Path.Combine(path, ".claude");
        if (!Directory.Exists(claude))
        {
            return new PreFlightResult.Rejected(RunFailureReason.UnknownProject, "no .claude");
        }

        if (job.Agent is { } agent)
        {
            // A separator or a space would make the existence check look outside .claude/agents/.
            if (!SafeAgentName().IsMatch(agent))
            {
                return new PreFlightResult.Rejected(RunFailureReason.UnknownAgent, "unsafe agent name");
            }

            if (!File.Exists(Path.Combine(claude, "agents", agent + ".md")))
            {
                return new PreFlightResult.Rejected(RunFailureReason.UnknownAgent, "unknown agent");
            }
        }

        return new PreFlightResult.Ok(path, job.Agent);
    }

    [GeneratedRegex(@"^[A-Za-z0-9._-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex SafeAgentName();
}
