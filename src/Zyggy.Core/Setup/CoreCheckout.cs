using System.Globalization;

using Zyggy.Core.Git;
using Zyggy.Core.Processes;

namespace Zyggy.Core.Setup;

/// <summary>
/// The <c>zyggy-core</c> item of <c>zyggy setup</c> (spec 42 AC-18, plan 42 A6/R6): clones the template on <c>main</c> when the folder is
/// absent (or empty), fast-forwards a clean checkout on <c>main</c>, leaves a dirty, other-branch or diverged checkout as it is, and refuses a
/// folder that is not a checkout of its own or has another <c>origin</c>. Git runs through <see cref="GitClient"/> only, never prompts
/// (<c>GIT_TERMINAL_PROMPT=0</c>, <c>GCM_INTERACTIVE=never</c>), speaks English (<c>LC_ALL=C</c>, so the diverged refusal text is recognised),
/// and nothing is ever reset, stashed or deleted.
/// </summary>
internal sealed class CoreCheckout(GitClient git)
{
    private const string Branch = "main";
    private const int ShortSha = 12;

    /// <summary>The checkout over <paramref name="processes"/>: <paramref name="gitEnvironment"/> plus <c>GCM_INTERACTIVE=never</c> and <c>LC_ALL=C</c> on every call.</summary>
    public static CoreCheckout Create(IProcessRunner processes, IReadOnlyDictionary<string, string?> gitEnvironment, TimeProvider clock)
    {
        var environment = new Dictionary<string, string?>(gitEnvironment) { ["GCM_INTERACTIVE"] = "never", ["LC_ALL"] = "C" };
        return new CoreCheckout(new GitClient(processes, new GitClientOptions { Environment = environment, Timeout = TimeSpan.FromMinutes(5) }, clock));
    }

    public async Task<SetupLine> ApplyAsync(string corePath, Uri remote, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(remote);
        string full = Path.GetFullPath(corePath);
        string url = remote.OriginalString;
        if (!Directory.Exists(full) || !Directory.EnumerateFileSystemEntries(full).Any())
        {
            return await CloneAsync(full, url, ct).ConfigureAwait(false);
        }

        // The folder must be the top of a work tree of its own: rev-parse prints the top level, then an empty prefix there.
        GitResult top = await git.RunAsync(full, ["rev-parse", "--show-toplevel", "--show-prefix"], null, ct).ConfigureAwait(false);
        string[] topLines = top.Stdout.Split('\n');
        if (!top.Succeeded || topLines.Length < 2 || topLines[1].Trim().Length > 0)
        {
            return Line(SetupOutcome.Failed, corePath + " is not a git checkout");
        }

        string? origin = await git.ConfigGetAsync(full, "remote.origin.url", ct).ConfigureAwait(false);
        if (!string.Equals(origin, url, StringComparison.Ordinal))
        {
            return Line(SetupOutcome.Failed, origin is null
                ? corePath + " has no origin, expected " + url
                : corePath + " has origin " + origin + ", expected " + url);
        }

        GitResult status = await git.StatusPorcelainAsync(full, ct).ConfigureAwait(false);
        if (!status.Succeeded)
        {
            return Line(SetupOutcome.Failed, FirstError(status));
        }

        if (status.Stdout.Length > 0)
        {
            return Line(SetupOutcome.LeftAsIs, "dirty working copy");
        }

        string? branch = await git.CurrentBranchAsync(full, ct).ConfigureAwait(false);
        if (branch is null)
        {
            return Line(SetupOutcome.LeftAsIs, "detached HEAD");
        }

        if (branch != Branch)
        {
            return Line(SetupOutcome.LeftAsIs, "on branch " + branch);
        }

        return await PullAsync(corePath, full, ct).ConfigureAwait(false);
    }

    private static SetupLine Line(SetupOutcome outcome, string detail) => new(SetupItem.ZyggyCore, outcome, detail);

    /// <summary>
    /// Git's first error line: the first <c>fatal:</c> or <c>error:</c> line (progress such as <c>Cloning into …</c> comes before it), else the
    /// first non-empty line; a process that never ran says so.
    /// </summary>
    private static string FirstError(GitResult result)
    {
        string[] lines = result.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string? error = lines.FirstOrDefault(l => l.StartsWith("fatal:", StringComparison.Ordinal) || l.StartsWith("error:", StringComparison.Ordinal))
            ?? lines.FirstOrDefault();
        if (result.ExitCode is null)
        {
            return error is null ? "git could not be started" : "git did not finish in time";
        }

        return error ?? "git exited with " + result.ExitCode.Value.ToString(CultureInfo.InvariantCulture);
    }

    private async Task<SetupLine> CloneAsync(string full, string url, CancellationToken ct)
    {
        string parent = Path.GetDirectoryName(full) ?? full;
        Directory.CreateDirectory(parent);
        GitResult clone = await git.RunAsync(parent, ["clone", "--branch", Branch, url, full], null, ct).ConfigureAwait(false);
        if (!clone.Succeeded)
        {
            return Line(SetupOutcome.Failed, FirstError(clone));
        }

        string? head = await git.RevParseAsync(full, "HEAD", ct).ConfigureAwait(false);
        return Line(SetupOutcome.Done, "cloned " + url + " at " + Short(head));
    }

    /// <summary>
    /// <c>pull --ff-only origin main</c>: <c>HEAD</c> unchanged means already up to date (compared by SHA, not by git's localised message); a
    /// refusal to fast-forward means diverged (plan 42 R6); any other failure is git's first error line.
    /// </summary>
    private async Task<SetupLine> PullAsync(string corePath, string full, CancellationToken ct)
    {
        string? before = await git.RevParseAsync(full, "HEAD", ct).ConfigureAwait(false);
        GitResult pull = await git.RunAsync(full, ["pull", "--ff-only", "origin", Branch], null, ct).ConfigureAwait(false);
        if (!pull.Succeeded)
        {
            return pull.ExitCode is not null && (pull.Stderr.Contains("fast-forward", StringComparison.OrdinalIgnoreCase)
                    || pull.Stderr.Contains("diverg", StringComparison.OrdinalIgnoreCase))
                ? Line(SetupOutcome.LeftAsIs, "diverged from origin/" + Branch)
                : Line(SetupOutcome.Failed, FirstError(pull));
        }

        string? after = await git.RevParseAsync(full, "HEAD", ct).ConfigureAwait(false);
        return before is not null && string.Equals(before, after, StringComparison.Ordinal)
            ? Line(SetupOutcome.AlreadyDone, "already up to date (" + corePath + ")")
            : Line(SetupOutcome.Done, "fast-forwarded to " + Short(after));
    }

    private static string Short(string? sha) => sha is null ? "an unknown commit" : sha[..Math.Min(ShortSha, sha.Length)];
}
