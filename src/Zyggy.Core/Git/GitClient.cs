using Zyggy.Core.Processes;

namespace Zyggy.Core.Git;

/// <summary>Settings of <see cref="GitClient"/>.</summary>
internal sealed record GitClientOptions
{
    public string Executable { get; init; } = "git";

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Environment additions for every git process; <c>GIT_TERMINAL_PROMPT=0</c> is always added.</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();
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
        RunAsync(repository, ["status", "--porcelain=v1", "--untracked-files=all"], null, cancellationToken);

    public Task<GitResult> AddAsync(string repository, IReadOnlyList<string> paths, CancellationToken cancellationToken) =>
        paths.Count == 0
            ? Task.FromResult(new GitResult(0, string.Empty, string.Empty))
            : RunAsync(repository, ["add", "--", .. paths], null, cancellationToken);

    public Task<GitResult> CommitOnlyAsync(string repository, string message, IReadOnlyList<string> paths, CancellationToken cancellationToken) =>
        RunAsync(repository, ["commit", "--only", "-F", "-", "--", .. paths], message, cancellationToken);

    public Task<GitResult> PushAsync(string repository, string branch, CancellationToken cancellationToken) =>
        RunAsync(repository, ["push", "origin", "HEAD:" + branch], null, cancellationToken);

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

    public async Task<GitResult> RunAsync(string repository, IReadOnlyList<string> arguments, string? standardInput, CancellationToken cancellationToken)
    {
        var environment = new Dictionary<string, string?>(options.Environment) { ["GIT_TERMINAL_PROMPT"] = "0" };
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
