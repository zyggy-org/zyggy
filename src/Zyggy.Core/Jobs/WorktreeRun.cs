using System.Globalization;

using Zyggy.Core.Envelope;
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
    /// A named job (spec 53) is first checked for the refusals: <see cref="PrepareResult.Refused"/> before any writing git command; a
    /// named job carrying <c>base</c> or <c>mandate</c> is refused there, so <paramref name="baseBranch"/> and <paramref name="named"/>
    /// never both apply. With <paramref name="baseBranch"/> (spec 18) the branch starts from that branch of the origin, never a local one;
    /// absent there is <c>base</c>. The start point is always a SHA, so git records no upstream in the project's config.
    /// </summary>
    public async Task<PrepareResult> PrepareAsync(string project, string worktree, string jobId, string? baseBranch, NamedBranch? named, CancellationToken ct)
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

        if (baseBranch is not null)
        {
            var start = await git.ResolveRefAsync(project, "refs/remotes/origin/" + baseBranch, ct).ConfigureAwait(false);
            return start is null
                ? new PrepareResult.Failed("base", fetchFailed)
                : await AddAsync(project, worktree, jobId, start, fetchFailed, defaultBranch, ct).ConfigureAwait(false);
        }

        // Spec 53 Prepare step 3: a named job starts at origin/<branch> when that remote-tracking ref exists (as fetched, or stale after a
        // failed fetch); a local branch of that name is never read.
        var baseSha = named is null ? null : await git.ResolveRefAsync(project, "refs/remotes/origin/" + named.Branch, ct).ConfigureAwait(false);
        if (baseSha is null)
        {
            defaultBranch = named is null ? await git.DefaultBranchAsync(project, ct).ConfigureAwait(false) : defaultBranch;
            baseSha = defaultBranch is null
                ? null
                : await git.ResolveRefAsync(project, "refs/remotes/origin/" + defaultBranch, ct).ConfigureAwait(false)
                  ?? await git.ResolveRefAsync(project, "refs/heads/" + defaultBranch, ct).ConfigureAwait(false);
        }

        if (baseSha is null)
        {
            return new PrepareResult.Failed("default_branch", fetchFailed);
        }

        return await AddAsync(project, worktree, jobId, baseSha, fetchFailed, defaultBranch, ct).ConfigureAwait(false);
    }

    private async Task<PrepareResult> AddAsync(string project, string worktree, string jobId, string baseSha, bool fetchFailed, string? defaultBranch, CancellationToken ct)
    {
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
        return added.Succeeded ? new PrepareResult.Prepared(baseSha, fetchFailed, defaultBranch) : new PrepareResult.Failed("worktree_add", fetchFailed);
    }

    /// <summary>
    /// Commit, push when ahead, clean up — after every model run that started (done, failed or timeout). For a named job (spec 53) a
    /// <c>done</c> run ahead of its start is pushed to the named branch on <c>origin</c> (never forced); every other case follows spec 16
    /// on <c>zyggy/&lt;id&gt;</c> and the body says the named branch was not changed. <paramref name="outcome"/> is ignored for an unnamed job.
    /// The node's commit subject is <paramref name="description"/> when given, its body <c>zyggy job &lt;id&gt;</c> (spec 53 slice 4); a pushed
    /// named branch behind <c>origin/&lt;defaultBranch&gt;</c> sets <see cref="WorktreeOutcome.BehindLine"/>. With <paramref name="baseBranch"/>
    /// (spec 18; never together with a named branch) the pushed tip also goes to that branch of the origin when git accepts it as a
    /// fast-forward; a refusal keeps <c>zyggy/&lt;id&gt;</c> as the result and names it, the status unchanged. The project's default branch
    /// is never pushed this way.
    /// </summary>
    public async Task<WorktreeOutcome> FinishAsync(
        string project,
        string worktree,
        string jobId,
        string baseSha,
        GitIdentity identity,
        ReportStatus outcome,
        string? namedBranch,
        string? description,
        string? defaultBranch,
        string? baseBranch,
        CancellationToken ct)
    {
        var branch = BranchFor(jobId);
        var status = await git.StatusPorcelainAsync(worktree, ct).ConfigureAwait(false);
        if (!status.Succeeded)
        {
            return NotChanged(WorktreeOutcome.Kept(worktree, "status"), namedBranch, "worktree kept");
        }

        if (status.Stdout.Length > 0)
        {
            var add = await git.AddAllAsync(worktree, ct).ConfigureAwait(false);
            var commit = add.Succeeded
                ? await git.CommitStagedAsync(worktree, CommitMessage(jobId, description), identity, ct).ConfigureAwait(false)
                : add;
            if (!commit.Succeeded)
            {
                return NotChanged(WorktreeOutcome.Kept(worktree, "commit"), namedBranch, "worktree kept");
            }
        }

        var tip = await git.RevParseAsync(project, "refs/heads/" + branch, ct).ConfigureAwait(false);
        var ahead = tip is null ? null : await git.CountAheadAsync(project, baseSha, tip, ct).ConfigureAwait(false);
        if (tip is null || ahead is null)
        {
            return NotChanged(WorktreeOutcome.Kept(worktree, "rev-parse"), namedBranch, "worktree kept");
        }

        if (ahead == 0)
        {
            var cleaned = await RemoveAsync(project, worktree, ct).ConfigureAwait(false);
            if (cleaned)
            {
                await git.DeleteBranchAsync(project, branch, ct).ConfigureAwait(false);
            }

            // Nothing to push: a done run writes no branch line (spec 16); a failed or timed-out one still says the named branch is unchanged.
            var empty = new WorktreeOutcome([], null, null, null) { RemoveFailed = !cleaned };
            return outcome == ReportStatus.Done ? empty : NotChanged(empty, namedBranch, RunEnded(outcome));
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

        // Spec 53 Finish step 2: only a done run is pushed to the named branch, by one non-forced push of zyggy/<id>.
        string? cause = null;
        if (namedBranch is not null)
        {
            cause = outcome != ReportStatus.Done ? RunEnded(outcome) : hasOrigin ? null : "no origin";
            if (cause is null)
            {
                var advanced = await git.PushBranchToAsync(project, branch, namedBranch, ct).ConfigureAwait(false);
                if (advanced.Succeeded)
                {
                    var gone = await RemoveAsync(project, worktree, ct).ConfigureAwait(false);
                    if (gone)
                    {
                        await git.DeleteBranchAsync(project, branch, ct).ConfigureAwait(false);
                    }

                    lines.Add("branch " + namedBranch + " pushed at " + tip);

                    // Spec 53 Finish, "Behind the default branch": one line when origin/<default> has commits the pushed tip lacks; no merge.
                    var behind = defaultBranch is null
                        ? null
                        : await git.CountAheadAsync(project, tip, "refs/remotes/origin/" + defaultBranch, ct).ConfigureAwait(false);
                    return new WorktreeOutcome(files, tip, string.Join('\n', lines), null)
                    {
                        RemoveFailed = !gone,
                        BehindLine = behind > 0
                            ? string.Create(CultureInfo.InvariantCulture, $"branch {namedBranch} is {behind} commits behind origin/{defaultBranch}")
                            : null,
                    };
                }

                cause = "push refused";
            }
        }

        // Spec 16 Finish on zyggy/<id> (spec 53 Finish step 3 for a named job).
        var pushed = hasOrigin && (await git.PushBranchAsync(project, branch, ct).ConfigureAwait(false)).Succeeded;
        var removed = await RemoveAsync(project, worktree, ct).ConfigureAwait(false);
        if (!pushed)
        {
            lines.Add("branch " + branch + " kept locally, not pushed: push");
            AddNotChanged(lines, namedBranch, cause);
            return new WorktreeOutcome(files, null, string.Join('\n', lines), "push") { RemoveFailed = !removed };
        }

        lines.Add("branch " + branch + " pushed at " + tip);
        if (baseBranch is not null)
        {
            // Before the local branch goes: the base push sends the same local ref.
            lines.Add(await PushToBaseAsync(project, branch, baseBranch, tip, ct).ConfigureAwait(false));
        }

        // git refuses to delete a branch a worktree still has checked out; the next run of this job id cleans both.
        if (removed)
        {
            await git.DeleteBranchAsync(project, branch, ct).ConfigureAwait(false);
        }

        AddNotChanged(lines, namedBranch, cause);

        // A done run whose named branch was not changed ends failed / git_error (spec 53, Finish table); the run's failure stays its own.
        var failedStep = namedBranch is not null && outcome == ReportStatus.Done ? "push" : null;
        return new WorktreeOutcome(files, tip, string.Join('\n', lines), failedStep) { RemoveFailed = !removed };
    }

    // Spec 18: never a default branch (the merge kind, which may, is not built); never forced, so a moved base is refused by git itself.
    private async Task<string> PushToBaseAsync(string project, string branch, string baseBranch, string tip, CancellationToken ct)
    {
        var defaultBranch = await git.DefaultBranchAsync(project, ct).ConfigureAwait(false);
        if (defaultBranch is null)
        {
            return "branch " + baseBranch + " not updated: default branch unknown";
        }

        if (string.Equals(defaultBranch, baseBranch, StringComparison.Ordinal))
        {
            return "branch " + baseBranch + " not updated: default branch";
        }

        var pushed = await git.PushBranchToAsync(project, branch, baseBranch, ct).ConfigureAwait(false);
        return pushed.Succeeded
            ? "branch " + baseBranch + " fast-forwarded to " + tip
            : "branch " + baseBranch + " not updated (moved meanwhile); the result is " + branch;
    }

    // Spec 53 slice 4: the description is the subject and "zyggy job <id>" the body; without it the message is today's.
    private static string CommitMessage(string jobId, string? description) =>
        description is null ? "zyggy job " + jobId + "\n" : description + "\n\nzyggy job " + jobId + "\n";

    private static string RunEnded(ReportStatus outcome) => "run ended " + EnvelopeWire.ToWire(outcome);

    private static void AddNotChanged(List<string> lines, string? namedBranch, string? cause)
    {
        if (namedBranch is not null && cause is not null)
        {
            lines.Add("branch " + namedBranch + " not changed: " + cause);
        }
    }

    private static WorktreeOutcome NotChanged(WorktreeOutcome outcome, string? namedBranch, string cause) =>
        namedBranch is null
            ? outcome
            : outcome with { ResultLines = (outcome.ResultLines is { } lines ? lines + "\n" : string.Empty) + "branch " + namedBranch + " not changed: " + cause };

    // A folder held open is removed at the next run of this job id, else by hand (spec 16 Failure modes); the report is unaffected.
    private async Task<bool> RemoveAsync(string project, string worktree, CancellationToken ct) =>
        (await git.WorktreeRemoveAsync(project, worktree, ct).ConfigureAwait(false)).Succeeded;
}

/// <summary>The outcome of <see cref="WorktreeRun.PrepareAsync"/>.</summary>
internal abstract record PrepareResult(bool FetchFailed)
{
    /// <summary>The worktree exists; <paramref name="BaseSha"/> is the commit the branch is compared with. <paramref name="DefaultBranch"/> is git's default branch as Prepare found it (spec 53, the "behind" line), or <see langword="null"/>.</summary>
    public sealed record Prepared(string BaseSha, bool FetchFailed, string? DefaultBranch = null) : PrepareResult(FetchFailed);

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

    /// <summary>Gets the line <c>branch &lt;name&gt; is &lt;n&gt; commits behind origin/&lt;default&gt;</c> for a pushed named branch with n &gt; 0 (spec 53), else <see langword="null"/>.</summary>
    public string? BehindLine { get; init; }

    public static WorktreeOutcome Kept(string worktree, string step) => new([], null, "worktree kept at " + worktree + ": " + step, step);
}
