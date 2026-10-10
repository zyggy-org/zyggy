using Zyggy.Core.Git;

namespace Zyggy.Core.Memory;

/// <summary>The preflight of a memory writer: the current branch, what blocks the write, and whether an earlier commit is still unpushed.</summary>
/// <param name="Branch">The current branch, or <see langword="null"/> on a detached HEAD.</param>
/// <param name="Detail"><c>not_on_branch</c>, <c>operation_in_progress</c>, or <see langword="null"/> when the write may go on.</param>
/// <param name="PushPending">Whether an unpushed <c>dream </c> or <c>archive </c> commit could still not be pushed.</param>
internal sealed record PublisherPreflight(string? Branch, string? Detail, bool PushPending);

/// <summary>The outcome of one commit and push.</summary>
/// <param name="Committed">Whether the commit exists.</param>
/// <param name="Sha">The commit's hash.</param>
/// <param name="Pushed">Whether the push succeeded.</param>
/// <param name="GitStep"><c>add_failed</c>, <c>commit_failed</c>, <c>index_lock</c>, or <see langword="null"/> when committed.</param>
internal sealed record PublishResult(bool Committed, string? Sha, bool Pushed, string? GitStep);

/// <summary>
/// The git path shared by the memory writers (spec 28 AC-19/AC-20, spec 37 AC-15/AC-16): the preflight that pushes an earlier
/// writer's deferred commit first, one <c>add</c> + <c>commit --only</c> of exactly the given paths, and the push with one
/// fetch-and-rebase retry, the rebase aborted on conflict, never a force push.
/// </summary>
internal sealed class MemoryPublisher(GitClient git)
{
    public async Task<PublisherPreflight> PreflightAsync(string repository, CancellationToken cancellationToken)
    {
        var branch = await git.CurrentBranchAsync(repository, cancellationToken).ConfigureAwait(false);
        if (branch is null)
        {
            return new PublisherPreflight(null, "not_on_branch", false);
        }

        if (await git.OperationInProgressAsync(repository, cancellationToken).ConfigureAwait(false))
        {
            return new PublisherPreflight(branch, "operation_in_progress", false);
        }

        var unpushed = await git.UnpushedCommitSubjectsAsync(repository, branch, cancellationToken).ConfigureAwait(false);
        var pending = unpushed.Any(s => s.StartsWith("dream ", StringComparison.Ordinal) || s.StartsWith("archive ", StringComparison.Ordinal))
                      && !await PushAsync(repository, branch, cancellationToken).ConfigureAwait(false);
        return new PublisherPreflight(branch, null, pending);
    }

    public async Task<PublishResult> CommitAndPushAsync(
        string repository,
        string branch,
        string message,
        IReadOnlyList<string> addPaths,
        IReadOnlyList<string> commitPaths,
        CancellationToken cancellationToken)
    {
        var add = await git.AddAsync(repository, addPaths, cancellationToken).ConfigureAwait(false);
        if (!add.Succeeded)
        {
            return new PublishResult(false, null, false, Step(add, "add_failed"));
        }

        var commit = await git.CommitOnlyAsync(repository, message, commitPaths, cancellationToken).ConfigureAwait(false);
        if (!commit.Succeeded)
        {
            return new PublishResult(false, null, false, Step(commit, "commit_failed"));
        }

        var sha = await git.RevParseAsync(repository, "HEAD", cancellationToken).ConfigureAwait(false);
        var (pushed, rebased) = await PushCoreAsync(repository, branch, cancellationToken).ConfigureAwait(false);

        // A clean rebase rewrote the commit: report the sha that is on the remote.
        if (rebased)
        {
            sha = await git.RevParseAsync(repository, "HEAD", cancellationToken).ConfigureAwait(false);
        }

        return new PublishResult(true, sha, pushed, null);
    }

    /// <summary><c>git rm</c> of exactly <paramref name="paths"/>; <see langword="null"/> on success, else <c>rm_failed</c> or <c>index_lock</c>.</summary>
    public async Task<string?> RemoveAsync(string repository, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var rm = await git.RemoveAsync(repository, paths, cancellationToken).ConfigureAwait(false);
        return rm.Succeeded ? null : Step(rm, "rm_failed");
    }

    /// <summary>
    /// Pushes; when rejected, fetches, rebases the local commits once and pushes again; a conflict aborts the rebase and keeps the
    /// commits for the next writer. Any other push failure also defers the push. Never a force push.
    /// </summary>
    public async Task<bool> PushAsync(string repository, string branch, CancellationToken cancellationToken) =>
        (await PushCoreAsync(repository, branch, cancellationToken).ConfigureAwait(false)).Pushed;

    private async Task<(bool Pushed, bool Rebased)> PushCoreAsync(string repository, string branch, CancellationToken cancellationToken)
    {
        var rebased = false;
        var push = await git.PushAsync(repository, branch, cancellationToken).ConfigureAwait(false);
        if (!push.Succeeded && IsRejected(push.Stderr))
        {
            var fetch = await git.FetchAsync(repository, cancellationToken).ConfigureAwait(false);
            var rebase = fetch.Succeeded ? await git.RebaseAsync(repository, branch, cancellationToken).ConfigureAwait(false) : fetch;
            if (rebase.Succeeded)
            {
                rebased = true;
                push = await git.PushAsync(repository, branch, cancellationToken).ConfigureAwait(false);
            }
            else if (fetch.Succeeded)
            {
                await git.RebaseAbortAsync(repository, cancellationToken).ConfigureAwait(false);
            }
        }

        return (push.Succeeded, rebased);
    }

    private static string Step(GitResult result, string step) => result.Stderr.Contains("index.lock", StringComparison.Ordinal) ? "index_lock" : step;

    private static bool IsRejected(string stderr) =>
        stderr.Contains("[rejected]", StringComparison.Ordinal) || stderr.Contains("non-fast-forward", StringComparison.Ordinal)
        || stderr.Contains("fetch first", StringComparison.Ordinal);
}
