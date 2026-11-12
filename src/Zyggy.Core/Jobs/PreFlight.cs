using System.Diagnostics.CodeAnalysis;

using Zyggy.Core.Envelope;
using Zyggy.Core.Git;
using Zyggy.Core.Registry;
using Zyggy.Core.Runs;

namespace Zyggy.Core.Jobs;

/// <summary>
/// The §6 pre-flight (step 1 as amended by A14-3): a <c>project</c> name resolves through the node's own scan
/// (<see cref="RegistrySnapshot.TryResolve"/>, never the bus copy); a path must lie under a <c>devRoots</c> entry; then the resolved folder
/// exists, is a git repository, has <c>.claude/</c>, and the named agent exists under <c>.claude/agents/</c>. Read-only: nothing is
/// created and nothing but <c>git rev-parse --git-dir</c> runs.
/// </summary>
internal static class PreFlight
{
    private static readonly char[] Separators = ['/', '\\'];

    public static async Task<PreFlightResult> CheckAsync(
        JobFields job,
        GitClient git,
        RegistrySnapshot registry,
        IReadOnlyList<string> devRoots,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(devRoots);

        if (!TryResolve(job.Project, registry, devRoots, out var path, out var refusal))
        {
            return new PreFlightResult.Rejected(RunFailureReason.UnknownProject, refusal);
        }

        if (!Directory.Exists(path))
        {
            return new PreFlightResult.Rejected(RunFailureReason.UnknownProject, "missing");
        }

        // A worktree's .git is a file, so git decides, not a directory check.
        if (await git.RevParseAsync(path, "--git-dir", ct).ConfigureAwait(false) is null)
        {
            return new PreFlightResult.Rejected(RunFailureReason.UnknownProject, "not a git repository");
        }

        var claude = Path.Join(path, ".claude");
        if (!Directory.Exists(claude))
        {
            return new PreFlightResult.Rejected(RunFailureReason.UnknownProject, "no .claude");
        }

        if (job.Agent is { } agent)
        {
            // A separator or a space would make the existence check look outside .claude/agents/.
            if (!AgentNames.IsSafe(agent))
            {
                return new PreFlightResult.Rejected(RunFailureReason.UnknownAgent, "unsafe agent name");
            }

            if (!File.Exists(Path.Join(claude, "agents", agent + ".md")))
            {
                return new PreFlightResult.Rejected(RunFailureReason.UnknownAgent, "unknown agent");
            }
        }

        return new PreFlightResult.Ok(path, job.Agent);
    }

    /// <summary>Step 1 alone: the folder this node may run in, or the check word that refused the value.</summary>
    private static bool TryResolve(
        string project,
        RegistrySnapshot registry,
        IReadOnlyList<string> devRoots,
        [NotNullWhen(true)] out string? path,
        [NotNullWhen(false)] out string? refusal)
    {
        path = null;
        refusal = null;
        if (!IsPathShaped(project))
        {
            if (registry.TryResolve(project, out var found))
            {
                path = Path.TrimEndingDirectorySeparator(found);
                return true;
            }

            refusal = "unknown name";
            return false;
        }

        if (Path.IsPathFullyQualified(project) && !project.Split(Separators).Any(s => s is "." or ".."))
        {
            var normalised = Normalise(project);
            if (devRoots.Any(root => Path.IsPathFullyQualified(root) && IsUnder(normalised, Normalise(root))))
            {
                path = normalised;
                return true;
            }
        }

        refusal = "outside devRoots";
        return false;
    }

    /// <summary>A rooted value, or a Windows drive shape (<c>C:</c>…) on any operating system, is a path; everything else is a name.</summary>
    private static bool IsPathShaped(string project) =>
        Path.IsPathRooted(project) || (project.Length >= 2 && char.IsAsciiLetter(project[0]) && project[1] == ':');

    private static string Normalise(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>Equal to the root, or below it on a separator boundary (so <c>/src2</c> is not under <c>/src</c>).</summary>
    private static bool IsUnder(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(path, root, comparison))
        {
            return true;
        }

        if (!path.StartsWith(root, comparison))
        {
            return false;
        }

        // A drive or file-system root keeps its trailing separator after trimming (C:\, /).
        if (Path.EndsInDirectorySeparator(root))
        {
            return true;
        }

        var next = path[root.Length];
        return next == Path.DirectorySeparatorChar || (OperatingSystem.IsWindows() && next == Path.AltDirectorySeparatorChar);
    }
}
