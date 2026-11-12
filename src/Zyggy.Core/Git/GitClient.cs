using System.Collections.ObjectModel;

using Zyggy.Core.Processes;

namespace Zyggy.Core.Git;

/// <summary>Settings of <see cref="GitClient"/>.</summary>
internal sealed record GitClientOptions
{
    public string Executable { get; init; } = "git";

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Environment additions for every git process; <c>GIT_TERMINAL_PROMPT=0</c> is always added.</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();

    /// <summary>When set, every <c>commit</c> is prefixed <c>-c user.name=… -c user.email=…</c> (deliverable 04); null keeps the repository's own identity (28's call sites).</summary>
    public GitIdentity? Identity { get; init; }
}

/// <summary>The outcome of one git command.</summary>
internal sealed record GitResult(int? ExitCode, string Stdout, string Stderr)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// The only place a git command line is built (spec 28 Contracts). Runs through <see cref="IProcessRunner"/>, never a shell, never a
/// prompt; failures are results, never exceptions.
/// </summary>
internal sealed class GitClient(IProcessRunner processes, GitClientOptions options, TimeProvider clock)
{
    public TimeProvider Clock { get; } = clock;

    public Task<GitResult> StatusPorcelainAsync(string repository, CancellationToken cancellationToken) =>
        RunAsync(repository, ["status", "--porcelain=v1", "-z", "--untracked-files=all"], null, cancellationToken);

    /// <summary>Restores paths from <c>HEAD</c> (pending-marker recovery).</summary>
    public Task<GitResult> CheckoutPathsAsync(string repository, IReadOnlyList<string> paths, CancellationToken cancellationToken) =>
        paths.Count == 0
            ? Task.FromResult(new GitResult(0, string.Empty, string.Empty))
            : RunAsync(repository, ["checkout", "HEAD", "--", .. paths], null, cancellationToken);

    public async Task<IReadOnlyList<GitStatusEntry>> StatusEntriesAsync(string repository, CancellationToken cancellationToken)
    {
        var result = await StatusPorcelainAsync(repository, cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? GitStatus.Parse(result.Stdout) : [];
    }

    public Task<GitResult> AddAsync(string repository, IReadOnlyList<string> paths, CancellationToken cancellationToken) =>
        paths.Count == 0
            ? Task.FromResult(new GitResult(0, string.Empty, string.Empty))
            : RunAsync(repository, ["add", "--", .. paths], null, cancellationToken);

    public Task<GitResult> RemoveAsync(string repository, IReadOnlyList<string> paths, CancellationToken cancellationToken) =>
        RunAsync(repository, ["rm", "--", .. paths], null, cancellationToken);

    public Task<GitResult> CommitOnlyAsync(string repository, string message, IReadOnlyList<string> paths, CancellationToken cancellationToken) =>
        RunAsync(repository, [.. IdentityArguments(), "commit", "--only", "-F", "-", "--", .. paths], message, cancellationToken);

    /// <summary>One logical move (§4 state machine: <c>git mv</c>, never copy + delete).</summary>
    public Task<GitResult> MoveAsync(string repository, string from, string to, CancellationToken cancellationToken) =>
        RunAsync(repository, ["mv", "--", from, to], null, cancellationToken);

    /// <summary>Parks a dirty working copy under a named stash (§5 start-up rule).</summary>
    public Task<GitResult> StashPushAsync(string repository, string message, CancellationToken cancellationToken) =>
        RunAsync(repository, ["stash", "push", "--include-untracked", "-m", message], null, cancellationToken);

    /// <summary>Reachability check of a remote branch through git itself (the proxy, TLS backend and credential helper are git's); <paramref name="environment"/> adds e.g. <c>GIT_SSH_COMMAND</c>.</summary>
    public Task<GitResult> LsRemoteAsync(string repository, Uri url, string branch, IReadOnlyDictionary<string, string?> environment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        return RunAsync(repository, ["ls-remote", "--exit-code", url.ToString(), "refs/heads/" + branch], null, environment, cancellationToken);
    }

    /// <summary>Authenticates against the remote's receive side without writing anything (<c>push --dry-run</c>).</summary>
    public Task<GitResult> PushDryRunAsync(string repository, Uri url, string refspec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        return RunAsync(repository, ["push", "--dry-run", url.ToString(), refspec], null, cancellationToken);
    }

    /// <summary>Unstages a path without touching the working copy (<c>rm --cached</c>); used to undo a failed write's <c>add</c> (deliverable 04).</summary>
    public Task<GitResult> UnstageAsync(string repository, string path, CancellationToken cancellationToken) =>
        RunAsync(repository, ["rm", "--cached", "--quiet", "--", path], null, cancellationToken);

    /// <summary>The value of a git configuration key, or <see langword="null"/> when unset.</summary>
    public async Task<string?> ConfigGetAsync(string repository, string key, CancellationToken cancellationToken)
    {
        var result = await RunAsync(repository, ["config", "--get", key], null, cancellationToken).ConfigureAwait(false);
        return result.Succeeded && result.Stdout.Trim() is { Length: > 0 } value ? value : null;
    }

    public Task<GitResult> PushAsync(string repository, string branch, CancellationToken cancellationToken) =>
        RunAsync(repository, ["push", "origin", "HEAD:" + branch], null, cancellationToken);

    /// <summary>Step 1 of the §5 write sequence: <c>pull --rebase origin &lt;branch&gt;</c> (deliverable 04).</summary>
    public Task<GitResult> PullRebaseAsync(string repository, string branch, CancellationToken cancellationToken) =>
        RunAsync(repository, ["pull", "--rebase", "origin", branch], null, cancellationToken);

    public async Task<string?> RevParseAsync(string repository, string revision, CancellationToken cancellationToken)
    {
        var result = await RunAsync(repository, ["rev-parse", revision], null, cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? result.Stdout.Trim() : null;
    }

    /// <summary>The current branch, or <see langword="null"/> on a detached HEAD.</summary>
    public async Task<string?> CurrentBranchAsync(string repository, CancellationToken cancellationToken)
    {
        var result = await RunAsync(repository, ["symbolic-ref", "--short", "-q", "HEAD"], null, cancellationToken).ConfigureAwait(false);
        return result.Succeeded && result.Stdout.Trim() is { Length: > 0 } branch ? branch : null;
    }

    /// <summary>
    /// The repository's default branch (spec 14, P7): <c>origin/HEAD</c> without its remote prefix, else a local <c>main</c>, else a
    /// local <c>master</c>, else the current branch, else <see langword="null"/>. Reading <c>origin/HEAD</c> first keeps a feature
    /// checkout from changing the answer.
    /// </summary>
    public async Task<string?> DefaultBranchAsync(string repository, CancellationToken cancellationToken)
    {
        var originHead = await RunAsync(repository, ["symbolic-ref", "-q", "--short", "refs/remotes/origin/HEAD"], null, cancellationToken).ConfigureAwait(false);
        if (originHead.Succeeded && originHead.Stdout.Trim() is { Length: > 0 } remoteHead)
        {
            const string Prefix = "origin/";
            string branch = remoteHead.StartsWith(Prefix, StringComparison.Ordinal) ? remoteHead[Prefix.Length..] : remoteHead;
            if (branch.Length > 0)
            {
                return branch;
            }
        }

        foreach (string candidate in (string[])["main", "master"])
        {
            var local = await RunAsync(repository, ["rev-parse", "--verify", "-q", "refs/heads/" + candidate], null, cancellationToken).ConfigureAwait(false);
            if (local.Succeeded)
            {
                return candidate;
            }
        }

        return await CurrentBranchAsync(repository, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The SHA a fully qualified ref points at (<c>rev-parse --verify -q</c>), or <see langword="null"/> when it does not exist.</summary>
    public async Task<string?> ResolveRefAsync(string repository, string reference, CancellationToken cancellationToken)
    {
        var result = await RunAsync(repository, ["rev-parse", "--verify", "-q", reference], null, cancellationToken).ConfigureAwait(false);
        return result.Succeeded && result.Stdout.Trim() is { Length: > 0 } sha ? sha : null;
    }

    /// <summary>Drops the records of worktrees whose folders are gone (spec 16).</summary>
    public Task<GitResult> WorktreePruneAsync(string repository, CancellationToken cancellationToken) =>
        RunAsync(repository, ["worktree", "prune"], null, cancellationToken);

    /// <summary>
    /// Adds a worktree at <paramref name="path"/> (spec 16): on a new <paramref name="branch"/> created from <paramref name="startPoint"/>,
    /// or, when <paramref name="startPoint"/> is <see langword="null"/>, on the existing branch as it stands.
    /// </summary>
    public Task<GitResult> WorktreeAddAsync(string repository, string path, string branch, string? startPoint, CancellationToken cancellationToken) =>
        startPoint is null
            ? RunAsync(repository, ["worktree", "add", path, branch], null, cancellationToken)
            : RunAsync(repository, ["worktree", "add", "-b", branch, path, startPoint], null, cancellationToken);

    /// <summary>
    /// Removes a clean worktree and its folder, ignored files included (<c>worktree remove</c>, never forced: a worktree git still sees as
    /// dirty stays, spec 16 "stale worktree folder").
    /// </summary>
    public Task<GitResult> WorktreeRemoveAsync(string repository, string path, CancellationToken cancellationToken) =>
        RunAsync(repository, ["worktree", "remove", path], null, cancellationToken);

    /// <summary>Stages every change, untracked files included, <c>.gitignore</c> respected (<c>add -A</c>).</summary>
    public Task<GitResult> AddAllAsync(string repository, CancellationToken cancellationToken) =>
        RunAsync(repository, ["add", "-A"], null, cancellationToken);

    /// <summary>
    /// Commits what is staged with <paramref name="identity"/> passed on the command, commit signing off and the repository's commit hooks
    /// skipped (spec 16 D6: an unattended commit can answer no prompt); the repository's config is never written.
    /// </summary>
    public Task<GitResult> CommitStagedAsync(string repository, string message, GitIdentity identity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return RunAsync(
            repository,
            ["-c", "user.name=" + identity.Name, "-c", "user.email=" + identity.Email, "-c", "commit.gpgsign=false", "commit", "--no-verify", "-F", "-"],
            message,
            cancellationToken);
    }

    /// <summary>How many commits <paramref name="tip"/> has that <paramref name="baseRevision"/> lacks, or <see langword="null"/> when git fails.</summary>
    public async Task<int?> CountAheadAsync(string repository, string baseRevision, string tip, CancellationToken cancellationToken)
    {
        var result = await RunAsync(repository, ["rev-list", "--count", baseRevision + ".." + tip], null, cancellationToken).ConfigureAwait(false);
        return result.Succeeded && int.TryParse(result.Stdout.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count)
            ? count
            : null;
    }

    /// <summary>The repository-relative paths changed between the merge base of the two revisions and <paramref name="tip"/>, a rename as a delete plus an add; <see langword="null"/> when git fails.</summary>
    public async Task<IReadOnlyList<string>?> ChangedPathsAsync(string repository, string baseRevision, string tip, CancellationToken cancellationToken)
    {
        var result = await RunAsync(repository, ["diff", "--name-only", "-z", "--no-renames", baseRevision + "..." + tip], null, cancellationToken).ConfigureAwait(false);
        // The process runner ends captured stdout with a line end after git's last NUL; it is not a path.
        return result.Succeeded ? result.Stdout.TrimEnd('\r', '\n').Split('\0', StringSplitOptions.RemoveEmptyEntries) : null;
    }

    /// <summary>Pushes the local <paramref name="branch"/> to the same name on <c>origin</c>; never forced.</summary>
    public Task<GitResult> PushBranchAsync(string repository, string branch, CancellationToken cancellationToken) =>
        RunAsync(repository, ["push", "origin", "refs/heads/" + branch + ":refs/heads/" + branch], null, cancellationToken);

    /// <summary>Deletes a local branch whether or not it is merged (<c>branch -D</c>).</summary>
    public Task<GitResult> DeleteBranchAsync(string repository, string branch, CancellationToken cancellationToken) =>
        RunAsync(repository, ["branch", "-D", branch], null, cancellationToken);

    public Task<GitResult> FetchAsync(string repository, CancellationToken cancellationToken) =>
        RunAsync(repository, ["fetch", "origin"], null, cancellationToken);

    public Task<GitResult> RebaseAsync(string repository, string branch, CancellationToken cancellationToken) =>
        RunAsync(repository, ["-c", "rebase.autoStash=true", "rebase", "origin/" + branch], null, cancellationToken);

    public Task<GitResult> RebaseAbortAsync(string repository, CancellationToken cancellationToken) =>
        RunAsync(repository, ["rebase", "--abort"], null, cancellationToken);

    /// <summary>Whether a rebase or merge is in progress (<c>rebase-merge</c>, <c>rebase-apply</c> or <c>MERGE_HEAD</c> exists).</summary>
    public async Task<bool> OperationInProgressAsync(string repository, CancellationToken cancellationToken)
    {
        var result = await RunAsync(repository, ["rev-parse", "--git-path", "rebase-merge", "--git-path", "rebase-apply", "--git-path", "MERGE_HEAD"],
            null, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return false;
        }

        return result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => Path.IsPathRooted(p) ? p : Path.Join(repository, p))
            .Any(p => Directory.Exists(p) || File.Exists(p));
    }

    /// <summary>The subjects of the local commits the remote branch lacks.</summary>
    public async Task<IReadOnlyList<string>> UnpushedCommitSubjectsAsync(string repository, string branch, CancellationToken cancellationToken)
    {
        var result = await RunAsync(repository, ["rev-list", "--format=%s", "origin/" + branch + "..HEAD"], null, cancellationToken).ConfigureAwait(false);
        return result.Succeeded
            ? result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => !l.StartsWith("commit ", StringComparison.Ordinal)).ToList()
            : [];
    }

    /// <summary>Runs one git command; a command that hits <c>index.lock</c> is retried three times with a two-second back-off.</summary>
    public Task<GitResult> RunAsync(string repository, IReadOnlyList<string> arguments, string? standardInput, CancellationToken cancellationToken) =>
        RunAsync(repository, arguments, standardInput, null, cancellationToken);

    /// <summary>Runs one git command with extra environment entries merged over the options'; the same index.lock retry.</summary>
    public async Task<GitResult> RunAsync(string repository, IReadOnlyList<string> arguments, string? standardInput, IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken)
    {
        var result = await RunOnceAsync(repository, arguments, standardInput, environment, cancellationToken).ConfigureAwait(false);
        for (var retry = 0; retry < 3 && !result.Succeeded && result.Stderr.Contains("index.lock", StringComparison.Ordinal); retry++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), Clock, cancellationToken).ConfigureAwait(false);
            result = await RunOnceAsync(repository, arguments, standardInput, environment, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    private IEnumerable<string> IdentityArguments() =>
        options.Identity is { } identity
            ? ["-c", "user.name=" + identity.Name, "-c", "user.email=" + identity.Email]
            : [];

    private async Task<GitResult> RunOnceAsync(string repository, IReadOnlyList<string> arguments, string? standardInput, IReadOnlyDictionary<string, string?>? extra, CancellationToken cancellationToken)
    {
        var environment = new Dictionary<string, string?>(options.Environment);
        foreach (var entry in extra ?? ReadOnlyDictionary<string, string?>.Empty)
        {
            environment[entry.Key] = entry.Value;
        }

        environment["GIT_TERMINAL_PROMPT"] = "0";
        var spec = new ProcessSpec(options.Executable, arguments, repository)
        {
            Environment = environment,
            StandardInput = standardInput,
            Timeout = options.Timeout,
        };
        var result = await processes.RunAsync(spec, cancellationToken).ConfigureAwait(false);
        return new GitResult(result.StartFailed || result.TimedOut ? null : result.ExitCode, result.Stdout, result.Stderr);
    }
}
