using System.Globalization;

using Zyggy.Core.Git;

namespace Zyggy.Core.Jobs;

/// <summary>
/// The worktree side of a <c>worktree: true</c> job (spec 16, founding spec §6 Pre-flight step 4): <see cref="PrepareAsync"/> creates
/// <c>&lt;nodeDir&gt;/worktrees/&lt;tenant&gt;/&lt;id&gt;</c> on branch <c>zyggy/&lt;id&gt;</c>; <see cref="FinishAsync"/> commits what the model
/// left, pushes the branch when it is ahead of its base, and removes the worktree. Every git command goes through <see cref="GitClient"/>;
/// failures are outcomes, never exceptions. Writes only the worktree folder, the branch <c>zyggy/&lt;id&gt;</c> and git's worktree records.
/// </summary>
internal sealed class WorktreeRun(GitClient git)
{
    /// <summary>The most paths a report lists (spec 16 AC-4).</summary>
    public const int MaxFilesChanged = 200;

    public static string BranchFor(string jobId) => "zyggy/" + jobId;

    /// <summary>
    /// Fetch (best effort), base, stale clean-up and <c>worktree add</c>. The model may start only on <see cref="PrepareResult.Prepared"/>.
    /// A named job (spec 53) is first checked for the refusals: <see cref="PrepareResult.Refused"/> before any writing git command.
    /// </summary>
    public async Task<PrepareResult> PrepareAsync(string project, string worktree, string jobId, NamedBranch? named, CancellationToken ct)
    {
        // Spec 53 Prepare step 0: a branch on a mandate job is refused before the first git command.
        if (named is { CarriesMandate: true })
        {
            return new PrepareResult.Refused("branch with base or mandate");
        }

        var hasOrigin = await git.ConfigGetAsync(project, "remote.origin.url", ct).ConfigureAwait(false) is not null;

        // Spec 53 Prepare step 1: for a named job the default branch is checked before the fetch (git's name, then the node's registry
        // entry), without regard to letter case; an unnamed job keeps spec 16's order.
        string? defaultBranch = null;
        if (named is not null)
        {
            defaultBranch = await git.DefaultBranchAsync(project, ct).ConfigureAwait(false);
            foreach (var protectedName in (string?[])[defaultBranch, named.RegistryDefault])
            {
                if (protectedName is { Length: > 0 } && string.Equals(named.Branch, protectedName, StringComparison.OrdinalIgnoreCase))
                {
                    return new PrepareResult.Refused("default branch " + protectedName);
                }
            }
        }

        var fetchFailed = hasOrigin && !(await git.FetchAsync(project, ct).ConfigureAwait(false)).Succeeded;

        defaultBranch = named is null ? await git.DefaultBranchAsync(project, ct).ConfigureAwait(false) : defaultBranch;
        var baseSha = defaultBranch is null
            ? null
            : await git.ResolveRefAsync(project, "refs/remotes/origin/" + defaultBranch, ct).ConfigureAwait(false)
              ?? await git.ResolveRefAsync(project, "refs/heads/" + defaultBranch, ct).ConfigureAwait(false);
        if (baseSha is null)
        {
            return new PrepareResult.Failed("default_branch", fetchFailed);
        }

        // An interrupted run of the same job: its folder goes, its branch stays (spec 16 step 3).
        try
        {
            if (Directory.Exists(worktree))
            {
                Directory.Delete(worktree, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new PrepareResult.Failed("worktree_add", fetchFailed);
        }

        await git.WorktreePruneAsync(project, ct).ConfigureAwait(false);
        var branch = BranchFor(jobId);
        var existing = await git.ResolveRefAsync(project, "refs/heads/" + branch, ct).ConfigureAwait(false);
        var added = await git.WorktreeAddAsync(project, worktree, branch, existing is null ? baseSha : null, ct).ConfigureAwait(false);
        return added.Succeeded ? new PrepareResult.Prepared(baseSha, fetchFailed) : new PrepareResult.Failed("worktree_add", fetchFailed);
    }

    /// <summary>Commit, push when ahead, clean up — after every model run that started (done, failed or timeout).</summary>
    public async Task<WorktreeOutcome> FinishAsync(string project, string worktree, string jobId, string baseSha, GitIdentity identity, CancellationToken ct)
    {
        var branch = BranchFor(jobId);
        var status = await git.StatusPorcelainAsync(worktree, ct).ConfigureAwait(false);
        if (!status.Succeeded)
        {
            return WorktreeOutcome.Kept(worktree, "status");
        }

        if (status.Stdout.Length > 0)
        {
            var add = await git.AddAllAsync(worktree, ct).ConfigureAwait(false);
            var commit = add.Succeeded
                ? await git.CommitStagedAsync(worktree, "zyggy job " + jobId + "\n", identity, ct).ConfigureAwait(false)
                : add;
            if (!commit.Succeeded)
            {
                return WorktreeOutcome.Kept(worktree, "commit");
            }
        }

        var tip = await git.RevParseAsync(project, "refs/heads/" + branch, ct).ConfigureAwait(false);
        var ahead = tip is null ? null : await git.CountAheadAsync(project, baseSha, tip, ct).ConfigureAwait(false);
        if (tip is null || ahead is null)
        {
            return WorktreeOutcome.Kept(worktree, "rev-parse");
        }

        if (ahead == 0)
        {
            var cleaned = await RemoveAsync(project, worktree, ct).ConfigureAwait(false);
            if (cleaned)
            {
                await git.DeleteBranchAsync(project, branch, ct).ConfigureAwait(false);
            }

            return new WorktreeOutcome([], null, null, null) { RemoveFailed = !cleaned };
        }

        var changed = await git.ChangedPathsAsync(project, baseSha, tip, ct).ConfigureAwait(false) ?? [];
        var sorted = changed.Order(StringComparer.Ordinal).ToList();
        var files = sorted.Take(MaxFilesChanged).ToList();
        var lines = new List<string>();
        if (sorted.Count > MaxFilesChanged)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"files_changed: first {MaxFilesChanged} of {sorted.Count}"));
        }

        var hasOrigin = await git.ConfigGetAsync(project, "remote.origin.url", ct).ConfigureAwait(false) is not null;
        var pushed = hasOrigin && (await git.PushBranchAsync(project, branch, ct).ConfigureAwait(false)).Succeeded;
        var removed = await RemoveAsync(project, worktree, ct).ConfigureAwait(false);
        if (!pushed)
        {
            lines.Add("branch " + branch + " kept locally, not pushed: push");
            return new WorktreeOutcome(files, null, string.Join('\n', lines), "push") { RemoveFailed = !removed };
        }

        // git refuses to delete a branch a worktree still has checked out; the next run of this job id cleans both.
        if (removed)
        {
            await git.DeleteBranchAsync(project, branch, ct).ConfigureAwait(false);
        }

        lines.Add("branch " + branch + " pushed at " + tip);
        return new WorktreeOutcome(files, tip, string.Join('\n', lines), null) { RemoveFailed = !removed };
    }

    // A folder held open is removed at the next run of this job id, else by hand (spec 16 Failure modes); the report is unaffected.
    private async Task<bool> RemoveAsync(string project, string worktree, CancellationToken ct) =>
        (await git.WorktreeRemoveAsync(project, worktree, ct).ConfigureAwait(false)).Succeeded;
}

/// <summary>The outcome of <see cref="WorktreeRun.PrepareAsync"/>.</summary>
internal abstract record PrepareResult(bool FetchFailed)
{
    /// <summary>The worktree exists; <paramref name="BaseSha"/> is the commit the branch is compared with.</summary>
    public sealed record Prepared(string BaseSha, bool FetchFailed) : PrepareResult(FetchFailed);

    /// <summary>No worktree; the model must not start. <paramref name="Step"/> names the git step for the report body.</summary>
    public sealed record Failed(string Step, bool FetchFailed) : PrepareResult(FetchFailed);

    /// <summary>
    /// The named branch must not be touched (spec 53): <c>rejected</c> / <c>branch_refused</c>, nothing fetched, no worktree, the model
    /// must not start. <paramref name="Rule"/> is the body's rule (<c>branch with base or mandate</c>, <c>default branch &lt;name&gt;</c>).
    /// </summary>
    public sealed record Refused(string Rule) : PrepareResult(false);
}

/// <summary>A job's named branch as Prepare checks it (spec 53, internal; no public <c>Zyggy.Core</c> surface).</summary>
/// <param name="Branch">The job's <c>branch</c> value.</param>
/// <param name="CarriesMandate">Whether the job's front matter also has spec 18's <c>base</c> or <c>mandate</c> key.</param>
/// <param name="RegistryDefault">The <c>default_branch</c> of the job's project in the node's own registry snapshot, or <see langword="null"/>.</param>
internal sealed record NamedBranch(string Branch, bool CarriesMandate, string? RegistryDefault);

/// <summary>What a finished worktree run adds to the report (spec 16 Behavior).</summary>
/// <param name="FilesChanged">The changed paths, ordinal order, at most <see cref="WorktreeRun.MaxFilesChanged"/>.</param>
/// <param name="DiffRef">The pushed tip SHA, or <see langword="null"/> unless the push succeeded.</param>
/// <param name="ResultLines">The body lines after the summary, or <see langword="null"/> when the branch had no commits.</param>
/// <param name="FailedStep">The git step that failed (<c>git_error</c>), or <see langword="null"/>.</param>
internal sealed record WorktreeOutcome(IReadOnlyList<string> FilesChanged, string? DiffRef, string? ResultLines, string? FailedStep)
{
    /// <summary>Gets a value indicating whether <c>git worktree remove</c> failed (logged once; the report is unaffected).</summary>
    public bool RemoveFailed { get; init; }

    public static WorktreeOutcome Kept(string worktree, string step) => new([], null, "worktree kept at " + worktree + ": " + step, step);
}
