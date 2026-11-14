using Microsoft.Extensions.Logging;

using Zyggy.Core.Git;

namespace Zyggy.Core.Health;

/// <summary>
/// The commit this machine's <c>zyggy-core</c> checkout is on (founding spec §9 as amended by deliverable 15; spec 15 Health): the
/// <c>HEAD</c> of <c>agentCore.path</c>, read through <see cref="GitClient"/> at start and at every discovery interval, for the
/// <c>agentCoreSha</c> field of <c>/health</c>. Reported, never pinned. A missing folder, a folder that is no repository of its own, or a
/// repository without a commit gives <see langword="null"/> and one warning per change; nothing here throws but a cancellation, and the
/// health colour never depends on it.
/// </summary>
internal sealed partial class AgentCoreCommit
{
    private readonly GitClient _git;
    private readonly string _path;
    private readonly TimeSpan _interval;
    private readonly TimeProvider _clock;
    private readonly ILogger<AgentCoreCommit> _log;
    private string? _sha;
    private bool _warned;

    public AgentCoreCommit(GitClient git, string path, TimeSpan interval, TimeProvider clock, ILogger<AgentCoreCommit> log)
    {
        _git = git;
        _path = path;
        _interval = interval;
        _clock = clock;
        _log = log;
    }

    /// <summary>Gets the commit as last read (40 or 64 lowercase hex characters); <see langword="null"/> before the first read and when the folder is no checkout.</summary>
    public string? Sha => Volatile.Read(ref _sha);

    /// <summary>Reads the checkout's <c>HEAD</c> once.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when <see cref="Sha"/> is updated.</returns>
    public async Task RefreshAsync(CancellationToken ct)
    {
        string? sha = await ReadAsync(ct).ConfigureAwait(false);
        Volatile.Write(ref _sha, sha);
        if (sha is not null)
        {
            _warned = false;
        }
        else if (!_warned)
        {
            _warned = true;
            LogNotACheckout(_path);
        }
    }

    /// <summary>One read at start, then one per interval on the injected clock, until <paramref name="ct"/> is cancelled.</summary>
    /// <param name="ct">Cancellation token; cancellation ends the loop normally.</param>
    /// <returns>A task that completes when the loop ends.</returns>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await RefreshAsync(ct).ConfigureAwait(false);
            using var timer = new PeriodicTimer(_interval, _clock);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await RefreshAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // the host asked us to stop
        }
    }

    private async Task<string?> ReadAsync(CancellationToken ct)
    {
        try
        {
            // The folder must be a repository of its own: git would otherwise answer with the HEAD of a repository further up.
            string marker = Path.Join(_path, ".git");
            if (!Directory.Exists(marker) && !File.Exists(marker))
            {
                return null;
            }

            string? sha = await _git.ResolveRefAsync(_path, "HEAD", ct).ConfigureAwait(false);
            return sha is { Length: 40 or 64 } && sha.All(char.IsAsciiHexDigitLower) ? sha : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "agent-core: {Path} is not a git checkout")]
    private partial void LogNotACheckout(string path);
}
