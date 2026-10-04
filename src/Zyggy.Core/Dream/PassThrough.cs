using Zyggy.Core.Git;
using Zyggy.Core.Memory;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Dream;

/// <summary>Files the run commits as found: <see cref="Include"/>; files held back for a secret-pattern line: <see cref="Withheld"/>.</summary>
internal sealed record PassThroughResult(IReadOnlyList<string> Include, IReadOnlyList<string> Withheld, IReadOnlySet<string> Untracked);

/// <summary>Durable files someone else changed: read-only for the model; the unstaged ones are committed as found.</summary>
internal sealed record CarriedResult(IReadOnlySet<string> ReadOnly, IReadOnlyList<string> CommitAsFound, IReadOnlySet<string> Untracked);

/// <summary>
/// AC-25 and spec 28 "Concurrency with writers": <c>auto/</c> and <c>daily/</c> changes (Claude Code auto memory, the Stop hook) are
/// committed as found, never rewritten, unless a line matches a secret pattern; <c>inbox/</c> is never committed; durable files with
/// someone else's uncommitted changes are carried. Changes staged by someone else stay staged and out of the commit.
/// </summary>
internal static class PassThrough
{
    public static PassThroughResult Classify(
        IReadOnlyList<GitStatusEntry> status,
        Principal principal,
        MemorySnapshot snapshot,
        SecretPatterns secrets,
        IReadOnlySet<string> runPaths)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(runPaths);
        var include = new List<string>();
        var withheld = new List<string>();
        var untracked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (entry, relative) in Own(status, principal))
        {
            if (!(relative.StartsWith("auto/", StringComparison.Ordinal) || relative.StartsWith("daily/", StringComparison.Ordinal))
                || runPaths.Contains(relative) || !entry.UnstagedOnly)
            {
                continue;
            }

            var full = Path.Join(snapshot.Paths.PrincipalDirectory, relative);
            if (File.Exists(full) && File.ReadAllLines(full).Any(line => secrets.TryMatch(line, out _)))
            {
                withheld.Add(relative);
                continue;
            }

            include.Add(relative);
            if (entry.Untracked)
            {
                untracked.Add(relative);
            }
        }

        return new PassThroughResult(include, withheld, untracked);
    }

    public static CarriedResult Carried(IReadOnlyList<GitStatusEntry> status, Principal principal, MemoryPaths paths)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(paths);
        var readOnly = new HashSet<string>(StringComparer.Ordinal);
        var commit = new List<string>();
        var untracked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (entry, relative) in Own(status, principal))
        {
            if (paths.TryResolve(relative) is not { Succeeded: true, Area: MemoryArea.Durable or MemoryArea.Identity or MemoryArea.Legacy or MemoryArea.CategoryIndex })
            {
                continue;
            }

            readOnly.Add(relative);
            if (entry.UnstagedOnly)
            {
                commit.Add(relative);
                if (entry.Untracked)
                {
                    untracked.Add(relative);
                }
            }
        }

        return new CarriedResult(readOnly, commit, untracked);
    }

    private static IEnumerable<(GitStatusEntry Entry, string Relative)> Own(IReadOnlyList<GitStatusEntry> status, Principal principal)
    {
        var prefix = $"{principal.Tenant.Value}/{principal.User.Value}/";
        return status.Where(e => e.Path.StartsWith(prefix, StringComparison.Ordinal)).Select(e => (e, e.Path[prefix.Length..]));
    }
}
