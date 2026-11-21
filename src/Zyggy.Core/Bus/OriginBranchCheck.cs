using Zyggy.Core.Git;

namespace Zyggy.Core.Bus;

/// <summary>
/// The sender's check that a mandate's branch exists on the origin of the project (spec 59 slice 3, AC-6, Q5): asked only when this
/// machine holds a checkout of the project, with <c>git ls-remote --exit-code origin refs/heads/&lt;branch&gt;</c> in that checkout. A
/// branch the origin answers "not there" (exit 2) is refused; any other git failure (no network, no origin) lets the job go, and the
/// node's report then carries git's message. Never throws except for the caller's cancellation.
/// </summary>
/// <param name="git">The git client.</param>
/// <param name="localProject">The folder of this machine's checkout of a project, by registry name, or <see langword="null"/>.</param>
internal sealed class OriginBranchCheck(GitClient git, Func<string, CancellationToken, string?> localProject)
{
    /// <summary>Returns the refusal's detail for the first branch the origin does not have, or <see langword="null"/>.</summary>
    /// <param name="project">The project's registry name, as the mandate names it.</param>
    /// <param name="branches">The branches to look for.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<string?> MissingAsync(string project, IReadOnlyList<string> branches, CancellationToken ct)
    {
        if (branches.Count == 0 || localProject(project, ct) is not { } folder)
        {
            return null;
        }

        foreach (string branch in branches)
        {
            // --exit-code: 2 when the origin answered and has no such ref; every other failure is "could not tell".
            GitResult result = await git.RunAsync(folder, ["ls-remote", "--exit-code", "origin", "refs/heads/" + branch], null, ct).ConfigureAwait(false);
            if (result.ExitCode == 2)
            {
                return "'" + branch + "' does not exist on the origin of " + project;
            }
        }

        return null;
    }
}
