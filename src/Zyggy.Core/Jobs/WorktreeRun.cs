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
    /// absent there is <c>base</c>. The start point is always a SHA, so git records no upstream in the project's config. With
    /// <paramref name="continueFrom"/> (spec 59 slice 6) the start point is <c>origin/zyggy/&lt;continueFrom&gt;</c> as fetched, after the
    /// <c>base</c> check when both are given; a branch the origin does not have is <see cref="PrepareResult.Refused"/>.
    /// </summary>
    public async Task<PrepareResult> PrepareAsync(string project, string worktree, string jobId, string? baseBranch, NamedBranch? named, string? continueFrom, CancellationToken ct)
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

        var fetch = hasOrigin ? await git.FetchAsync(project, ct).ConfigureAwait(false) : null;
        var fetchFailed = fetch is { Succeeded: false };

        if (baseBranch is not null)
        {
            // Spec 59 slice 3 (AC-6): without -q git says why the ref does not resolve; a failed fetch's own line comes first, as the cause.
            var resolved = await git.RunAsync(project, ["rev-parse", "--verify", "refs/remotes/origin/" + baseBranch], null, ct).ConfigureAwait(false);
            var start = resolved.Succeeded ? resolved.Stdout.Trim() : null;
            if (start is not { Length: > 0 })
            {
                var message = FirstLine(fetchFailed ? fetch!.Stderr : null) ?? FirstLine(resolved.Stderr);
                return new PrepareResult.Failed("base", fetchFailed, "origin/" + baseBranch + " not found" + (message is null ? string.Empty : " (" + message + ")"));
            }

            if (continueFrom is null)
            {
                return await AddAsync(project, worktree, jobId, start, fetchFailed, defaultBranch, ct).ConfigureAwait(false);
            }
        }

        // Spec 59 slice 6 (AC-9): the continued job's branch on the origin is the start point; absent there, the run is refused before
        // any writing git command (rejected / branch_refused), as spec 53's refusals are. Spec 18's base rules apply at Finish.
        if (continueFrom is not null)
        {
            var continued = BranchFor(continueFrom);
            var continueSha = await git.ResolveRefAsync(project, "refs/remotes/origin/" + continued, ct).ConfigureAwait(false);
            return continueSha is null
                ? new PrepareResult.Refused("continue: " + continued + " not found", fetchFailed)
                : await AddAsync(project, worktree, jobId, continueSha, fetchFailed, defaultBranch, ct).ConfigureAwait(false);
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

    private static string? FirstLine(string? text) =>
        text?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);

    /// <summary>Git's first error line (<c>fatal:</c> / <c>error:</c>; git prints progress lines such as <c>Preparing worktree</c> first), else its first line.</summary>
    internal static string? ErrorLine(string? text) =>
        text?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("fatal:", StringComparison.Ordinal) || l.StartsWith("error:", StringComparison.Ordinal))
        ?? FirstLine(text);

    private async Task<PrepareResult> AddAsync(string project, string worktree, string jobId, string baseSha, bool fetchFailed, string? defaultBranch, CancellationToken ct)
    {
        // Spec 61 §5: an earlier run of the same job id left its folder. When it is this project's worktree on zyggy/<id>, its uncommitted
        // work is committed there first, then the folder goes through git; the branch stays, so this run starts from it. Any other folder
        // (no worktree, another repository's, another checkout) and anything that fails keeps the folder and says why; nothing is deleted.
        if (Directory.Exists(worktree) && await RecoverAsync(project, worktree, jobId, ct).ConfigureAwait(false) is { } why)
        {
            return new PrepareResult.Failed("worktree_add", fetchFailed, why) { KeptAt = worktree };
        }

        await git.WorktreePruneAsync(project, ct).ConfigureAwait(false);
        var branch = BranchFor(jobId);
        var existing = await git.ResolveRefAsync(project, "refs/heads/" + branch, ct).ConfigureAwait(false);
        var added = await git.WorktreeAddAsync(project, worktree, branch, existing is null ? baseSha : null, ct).ConfigureAwait(false);
        return added.Succeeded
            ? new PrepareResult.Prepared(baseSha, fetchFailed, defaultBranch)
            : new PrepareResult.Failed("worktree_add", fetchFailed, ErrorLine(added.Stderr) ?? ErrorLine(added.Stdout) ?? "git worktree add failed")
            {
                KeptAt = Directory.Exists(worktree) ? worktree : null,
            };
    }

    /// <summary>The message a recovered leftover's commit carries (spec 61 §5).</summary>
    public static string RecoveredMessage(string jobId) => "zyggy job " + jobId + " (recovered)\n";

    /// <summary>Whether <paramref name="folder"/> is a worktree of the repository at <paramref name="project"/> (same common git directory).</summary>
    public async Task<bool> IsWorktreeOfAsync(string project, string folder, CancellationToken ct)
    {
        if (!File.Exists(Path.Join(folder, ".git")))
        {
            return false;
        }

        string? mine = await CommonDirAsync(folder, ct).ConfigureAwait(false);
        string? theirs = await CommonDirAsync(project, ct).ConfigureAwait(false);
        return mine is not null && theirs is not null && SamePath(mine, theirs);
    }

    /// <summary>The absolute common git directory of the repository <paramref name="repository"/> belongs to; <see langword="null"/> when git does not say.</summary>
    public async Task<string?> CommonDirAsync(string repository, CancellationToken ct)
    {
        var result = await git.RunAsync(repository, ["rev-parse", "--path-format=absolute", "--git-common-dir"], null, ct).ConfigureAwait(false);
        return result.Succeeded && result.Stdout.Trim() is { Length: > 0 } dir ? dir : null;
    }

    /// <summary>
    /// Commits what <paramref name="worktree"/> holds uncommitted on its branch (<see cref="RecoveredMessage"/>); <see langword="null"/> when
    /// nothing was left uncommitted, else why not (git's first line).
    /// </summary>
    public async Task<string?> CommitLeftoverAsync(string worktree, string jobId, CancellationToken ct)
    {
        var status = await git.StatusPorcelainAsync(worktree, ct).ConfigureAwait(false);
        if (!status.Succeeded)
        {
            return "status: " + (ErrorLine(status.Stderr) ?? "git status failed");
        }

        if (status.Stdout.Length == 0)
        {
            return null;
        }

        if (Recovery is null)
        {
            return "commit: no identity to commit with";
        }

        var add = await git.AddAllAsync(worktree, ct).ConfigureAwait(false);
        var commit = add.Succeeded ? await git.CommitStagedAsync(worktree, RecoveredMessage(jobId), Recovery, ct).ConfigureAwait(false) : add;
        return commit.Succeeded ? null : "commit: " + (ErrorLine(commit.Stderr) ?? ErrorLine(commit.Stdout) ?? "git commit failed");
    }

    /// <summary>The identity of a recovery commit (the node's, as Finish's); without one, uncommitted leftover work is kept, never committed.</summary>
    public GitIdentity? Recovery { get; init; }

    /// <summary>
    /// Why <paramref name="folder"/> must be kept rather than committed or removed (spec 61 §5: only a worktree of
    /// <paramref name="project"/> on its own <c>zyggy/&lt;id&gt;</c> is ever committed, pushed or deleted); <see langword="null"/> when it is one.
    /// </summary>
    public async Task<string?> NotOursAsync(string project, string folder, string jobId, CancellationToken ct)
    {
        if (!File.Exists(Path.Join(folder, ".git")))
        {
            return "not a git worktree";
        }

        if (!await IsWorktreeOfAsync(project, folder, ct).ConfigureAwait(false))
        {
            return "not a worktree of " + project;
        }

        string branch = BranchFor(jobId);
        var head = await git.RunAsync(folder, ["symbolic-ref", "-q", "HEAD"], null, ct).ConfigureAwait(false);
        return head.Succeeded && head.Stdout.Trim() == "refs/heads/" + branch
            ? null
            : "checked out " + (head.Succeeded ? head.Stdout.Trim() : "a detached HEAD") + ", not " + branch;
    }

    private async Task<string?> RecoverAsync(string project, string worktree, string jobId, CancellationToken ct)
    {
        // A folder we cannot prove is this job's own worktree is never committed in or deleted: it is kept and listed.
        if (await NotOursAsync(project, worktree, jobId, ct).ConfigureAwait(false) is { } notOurs)
        {
            return notOurs;
        }

        if (await CommitLeftoverAsync(worktree, jobId, ct).ConfigureAwait(false) is { } notCommitted)
        {
            return notCommitted;
        }

        var removed = await git.WorktreeRemoveAsync(project, worktree, ct).ConfigureAwait(false);
        return removed.Succeeded ? null : ErrorLine(removed.Stderr) ?? ErrorLine(removed.Stdout) ?? "git worktree remove failed";
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a).Replace('\\', '/').TrimEnd('/'),
            Path.GetFullPath(b).Replace('\\', '/').TrimEnd('/'),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>
    /// Commit, push when ahead, clean up — after every model run that started (done, failed or timeout). For a named job (spec 53) a
    /// <c>done</c> run ahead of its start is pushed to the named branch on <c>origin</c> (never forced); every other case follows spec 16
    /// on <c>zyggy/&lt;id&gt;</c> and the body says the named branch was not changed. <paramref name="outcome"/> is ignored for an unnamed job.
    /// The node's commit subject is <paramref name="description"/> when given, its body <c>zyggy job &lt;id&gt;</c> (spec 53 slice 4); a pushed
    /// named branch behind <c>origin/&lt;defaultBranch&gt;</c> sets <see cref="WorktreeOutcome.BehindLine"/>. With <paramref name="baseBranch"/>
    /// (spec 18; never together with a named branch) the pushed tip of a <c>done</c> run also goes to that branch of the origin when git
    /// accepts it as a fast-forward; a refusal keeps <c>zyggy/&lt;id&gt;</c> as the result and names it, the status unchanged. A
    /// <c>failed</c> or <c>timeout</c> run never touches that branch (spec 59 AC-8). The project's default branch is never pushed this way.
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

        // Spec 62 §1: each step that can end in "worktree kept" is tried once more after 5 s; a second failure keeps the folder.
        var status = await TwiceAsync(() => git.StatusPorcelainAsync(worktree, ct), ct).ConfigureAwait(false);
        if (!status.Succeeded)
        {
            return await KeptAsync(project, worktree, branch, baseSha, "status", status, namedBranch, ct).ConfigureAwait(false);
        }

        if (status.Stdout.Length > 0)
        {
            var add = await TwiceAsync(() => git.AddAllAsync(worktree, ct), ct).ConfigureAwait(false);
            var commit = add.Succeeded
                ? await TwiceAsync(() => git.CommitStagedAsync(worktree, CommitMessage(jobId, description), identity, ct), ct).ConfigureAwait(false)
                : add;
            if (!commit.Succeeded)
            {
                return await KeptAsync(project, worktree, branch, baseSha, "commit", commit, namedBranch, ct).ConfigureAwait(false);
            }
        }

        var read = await ReadTipAsync(project, branch, baseSha, ct).ConfigureAwait(false);
        if (read.Ahead is null)
        {
            await Task.Delay(RetryWait, git.Clock, ct).ConfigureAwait(false);
            read = await ReadTipAsync(project, branch, baseSha, ct).ConfigureAwait(false);
        }

        if (read is not { Tip: { } tip, Ahead: { } ahead })
        {
            return await KeptAsync(project, worktree, branch, baseSha, "rev-parse", read.Result, namedBranch, ct).ConfigureAwait(false);
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
            // Spec 59 slice 5 (AC-8): only a done run moves base; a failed or timed-out run keeps its work on zyggy/<id> alone.
            // Before the local branch goes: the base push sends the same local ref.
            lines.Add(outcome == ReportStatus.Done
                ? await PushToBaseAsync(project, branch, baseBranch, tip, ct).ConfigureAwait(false)
                : "branch " + baseBranch + " not updated: run " + EnvelopeWire.ToWire(outcome));
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

    /// <summary>How long Finish waits before it tries a failed step once more (spec 62 §1, Q12).</summary>
    internal static readonly TimeSpan RetryWait = TimeSpan.FromSeconds(5);

    // Spec 62 §1: a failed step runs once more after RetryWait on the injected clock; no file (an index.lock or any other) is ever deleted.
    private async Task<GitResult> TwiceAsync(Func<Task<GitResult>> step, CancellationToken ct)
    {
        var first = await step().ConfigureAwait(false);
        if (first.Succeeded)
        {
            return first;
        }

        await Task.Delay(RetryWait, git.Clock, ct).ConfigureAwait(false);
        return await step().ConfigureAwait(false);
    }

    // The tip of zyggy/<id> and how many commits it has past the run's start (the count Finish makes today); Result is git's last answer.
    private async Task<TipRead> ReadTipAsync(string project, string branch, string baseSha, CancellationToken ct)
    {
        var tip = await git.RunAsync(project, ["rev-parse", "refs/heads/" + branch], null, ct).ConfigureAwait(false);
        if (!tip.Succeeded || tip.Stdout.Trim() is not { Length: > 0 } sha)
        {
            return new TipRead(null, null, tip);
        }

        var count = await git.RunAsync(project, ["rev-list", "--count", baseSha + ".." + sha], null, ct).ConfigureAwait(false);
        return count.Succeeded && int.TryParse(count.Stdout.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var ahead)
            ? new TipRead(sha, ahead, count)
            : new TipRead(sha, null, count);
    }

    private sealed record TipRead(string? Tip, int? Ahead, GitResult Result);

    // Spec 62 §1.3: the step failed twice. The folder and the branch stay; what the model committed goes to zyggy/<id> when its tip is
    // ahead of the start (never forced, never a named branch or base); the body names the step and git's first error line.
    private async Task<WorktreeOutcome> KeptAsync(string project, string worktree, string branch, string baseSha, string step, GitResult failed, string? namedBranch, CancellationToken ct)
    {
        var why = ErrorLine(failed.Stderr) ?? ErrorLine(failed.Stdout) ?? "git " + step + " failed";
        List<string> lines = ["git_error: " + step + " (" + why + ")", "worktree kept at " + worktree];
        string? pushedTip = null;
        if (step != "rev-parse" && await ReadTipAsync(project, branch, baseSha, ct).ConfigureAwait(false) is { Tip: { } tip, Ahead: > 0 })
        {
            var push = await git.ConfigGetAsync(project, "remote.origin.url", ct).ConfigureAwait(false) is null
                ? null
                : await git.PushBranchAsync(project, branch, ct).ConfigureAwait(false);
            if (push is { Succeeded: true })
            {
                pushedTip = tip;
                lines.Add("branch " + branch + " pushed at " + tip + " (without the uncommitted changes)");
            }
            else
            {
                var refused = push is null ? "no origin" : ErrorLine(push.Stderr) ?? ErrorLine(push.Stdout) ?? "git push failed";
                lines.Add("branch " + branch + " kept locally, not pushed: push (" + refused + ")");
            }
        }

        AddNotChanged(lines, namedBranch, "worktree kept");
        return new WorktreeOutcome([], pushedTip, string.Join('\n', lines), step) { KeptWhy = step + ": " + why };
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

    /// <summary>
    /// No worktree; the model must not start. <paramref name="Step"/> names the git step for the report body; <paramref name="Detail"/>,
    /// when given, follows it with git's own message (spec 59 slice 3).
    /// </summary>
    public sealed record Failed(string Step, bool FetchFailed, string? Detail = null) : PrepareResult(FetchFailed)
    {
        /// <summary>Gets the leftover folder that was kept (spec 61 §5: <c>worktree kept at &lt;path&gt;</c>), or <see langword="null"/>.</summary>
        public string? KeptAt { get; init; }
    }

    /// <summary>
    /// The named branch must not be touched (spec 53), or the continued branch is not on the origin (spec 59 slice 6): <c>rejected</c> /
    /// <c>branch_refused</c>, nothing written, no worktree, the model must not start. <paramref name="Rule"/> is the body's rule
    /// (<c>branch with base or mandate</c>, <c>default branch &lt;name&gt;</c>, <c>continue: zyggy/&lt;id&gt; not found</c>).
    /// </summary>
    public sealed record Refused(string Rule, bool FetchFailed = false) : PrepareResult(FetchFailed);
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

    /// <summary>Gets why Finish kept the worktree folder (spec 62: <c>&lt;step&gt;: &lt;git's line&gt;</c>, for <c>kept-worktrees.txt</c>), or <see langword="null"/> when it did not.</summary>
    public string? KeptWhy { get; init; }
}
