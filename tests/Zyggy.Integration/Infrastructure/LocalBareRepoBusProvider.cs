using Zyggy.Core.Bus;
using Zyggy.Core.Git;

namespace Zyggy.Integration.Infrastructure;

/// <summary>
/// The test double of <see cref="IBusProvider"/> (spec 04 Decision Table): the "doorbell" is <c>git rev-parse &lt;branch&gt;</c>
/// in the bare repository compared with the caller's marker; pull and push run through the production <see cref="GitClient"/>
/// on the clone. It passes the same contract suite as the product provider.
/// </summary>
internal sealed class LocalBareRepoBusProvider(GitClient git, string bareDirectory, string cloneDirectory, string branch) : IBusProvider
{
    public BusRemote Remote { get; } = new("local", Path.GetFileName(bareDirectory), branch, new Uri(new Uri(bareDirectory + Path.DirectorySeparatorChar).AbsoluteUri), null);

    public Uri ApiBaseUri => Remote.HttpsUrl!;

    public async Task<HeadProbe> ProbeHeadAsync(string? etag, CancellationToken ct)
    {
        string? sha = Directory.Exists(bareDirectory) ? await git.RevParseAsync(bareDirectory, branch, ct).ConfigureAwait(false) : null;
        if (sha is null)
        {
            return new Unreachable("rev-parse failed");
        }

        return string.Equals(sha, etag, StringComparison.Ordinal) ? new Unchanged(null) : new Changed(sha, null);
    }

    public async Task<BusSyncResult> PullAsync(CancellationToken ct)
    {
        GitResult result = await git.PullRebaseAsync(cloneDirectory, branch, ct).ConfigureAwait(false);
        return new BusSyncResult(result.Succeeded, result.Succeeded ? string.Empty : result.Stderr);
    }

    public async Task<PushResult> PushAsync(CancellationToken ct)
    {
        GitResult result = await git.PushAsync(cloneDirectory, branch, ct).ConfigureAwait(false);
        if (result.Succeeded)
        {
            return new PushSucceeded();
        }

        return result.Stderr.Contains("[rejected]", StringComparison.Ordinal) || result.Stderr.Contains("non-fast-forward", StringComparison.Ordinal)
            ? new Rejected(result.Stderr)
            : new PushFailed(result.Stderr);
    }
}
