using System.Globalization;

using Microsoft.Extensions.Logging;

using Zyggy.Core.Envelope;
using Zyggy.Core.Git;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Jobs;

/// <summary>A leftover worktree folder the clean-up kept (spec 61 §5): the job id, the folder, and why.</summary>
/// <param name="Id">The job id the folder is named after.</param>
/// <param name="Path">The folder.</param>
/// <param name="Why">Why it was kept (the failed step and git's first error line, or <c>project not found</c>).</param>
internal sealed record KeptWorktree(string Id, string Path, string Why);

/// <summary>
/// Spec 61 §5 "Leftovers of finished jobs": at start-up and after each run, every folder under <c>&lt;nodeDir&gt;/worktrees/&lt;tenant&gt;/</c>
/// whose id has no claim and no live run is cleaned, never lost — uncommitted work is committed on its own <c>zyggy/&lt;id&gt;</c>, commits
/// the origin lacks are pushed to <c>origin/zyggy/&lt;id&gt;</c> (never forced, never another branch), and only then are the folder and the
/// local branch removed. A folder that cannot be committed, pushed or removed is kept whole, one Warning per folder per process, and
/// listed in <c>&lt;nodeDir&gt;/work/&lt;tenant&gt;/kept-worktrees.txt</c> for <c>zyggy status</c>. Never throws for a git or file failure.
/// </summary>
internal sealed partial class LeftoverWorktrees(GitClient git, string nodeDir, TenantId tenant, GitIdentity identity, ILogger<LeftoverWorktrees> log)
{
    // One clean-up at a time: a run that ends while another clean-up is under way leaves it to that one (and to the next run's).
    private int _cleaning;

    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);

    /// <summary>The folder the worktrees of <paramref name="tenant"/> live in.</summary>
    public static string RootFor(string nodeDir, TenantId tenant) => Path.Join(nodeDir, "worktrees", tenant.Value);

    /// <summary>The list of kept folders <c>zyggy status</c> reads.</summary>
    public static string KeptFileFor(string nodeDir, TenantId tenant) => Path.Join(nodeDir, "work", tenant.Value, "kept-worktrees.txt");

    /// <summary>Cleans every leftover folder whose id <paramref name="busy"/> does not name; returns (and records) the folders kept.</summary>
    public async Task<IReadOnlyList<KeptWorktree>> CleanAsync(Func<string, bool> busy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(busy);
        if (Interlocked.Exchange(ref _cleaning, 1) == 1)
        {
            return ReadKept(nodeDir, tenant);
        }

        try
        {
            var kept = new List<KeptWorktree>();
            foreach (string folder in Folders())
            {
                string id = Path.GetFileName(folder);
                if (!EnvelopeId.TryParse(id, out _) || busy(id))
                {
                    continue;
                }

                if (await CleanOneAsync(folder, id, ct).ConfigureAwait(false) is { } why)
                {
                    kept.Add(new KeptWorktree(id, folder, why));
                    if (_warned.Add(id))
                    {
                        LogKept(id, folder, why);
                    }
                }
                else
                {
                    LogCleaned(id);
                }
            }

            WriteKept(nodeDir, tenant, kept);
            return kept;
        }
        finally
        {
            Volatile.Write(ref _cleaning, 0);
        }
    }

    /// <summary>The kept folders as the last clean-up recorded them; empty when none (or unreadable).</summary>
    public static IReadOnlyList<KeptWorktree> ReadKept(string nodeDir, TenantId tenant)
    {
        try
        {
            string file = KeptFileFor(nodeDir, tenant);
            return File.Exists(file)
                ? [.. File.ReadAllLines(file).Select(l => l.Split('\t', 3)).Where(p => p.Length == 3).Select(p => new KeptWorktree(p[0], p[1], p[2]))]
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Records <paramref name="kept"/> (removes the list when empty); a list that cannot be written is left as it was.</summary>
    public static void WriteKept(string nodeDir, TenantId tenant, IReadOnlyList<KeptWorktree> kept)
    {
        ArgumentNullException.ThrowIfNull(kept);
        string file = KeptFileFor(nodeDir, tenant);
        try
        {
            if (kept.Count == 0)
            {
                File.Delete(file);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            string temp = file + ".tmp";
            File.WriteAllText(temp, string.Concat(kept.Select(k => k.Id + "\t" + k.Path + "\t" + OneLine(k.Why) + "\n")));
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // status shows the previous list; the next clean-up writes it again
        }
    }

    private static string OneLine(string text) => text.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    private List<string> Folders()
    {
        try
        {
            string root = RootFor(nodeDir, tenant);
            return Directory.Exists(root) ? [.. Directory.GetDirectories(root).Order(StringComparer.Ordinal)] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // null = cleaned (folder and branch gone, work on the origin); otherwise why the folder was kept.
    private async Task<string?> CleanOneAsync(string folder, string id, CancellationToken ct)
    {
        var worktrees = new WorktreeRun(git) { Recovery = identity };
        if (ProjectOf(folder) is not { } project)
        {
            return File.Exists(Path.Join(folder, ".git")) ? "project not found" : "not a git worktree";
        }

        if (!await worktrees.IsWorktreeOfAsync(project, folder, ct).ConfigureAwait(false))
        {
            return "not a worktree of " + project;
        }

        string branch = WorktreeRun.BranchFor(id);
        var head = await git.RunAsync(folder, ["symbolic-ref", "-q", "HEAD"], null, ct).ConfigureAwait(false);
        if (!head.Succeeded || head.Stdout.Trim() != "refs/heads/" + branch)
        {
            // Only the job's own branch is ever committed, pushed or deleted here.
            return "checked out " + (head.Succeeded ? head.Stdout.Trim() : "a detached HEAD") + ", not " + branch;
        }

        if (await worktrees.CommitLeftoverAsync(folder, id, ct).ConfigureAwait(false) is { } notCommitted)
        {
            return notCommitted;
        }

        var ahead = await git.RunAsync(project, ["rev-list", "--count", "refs/heads/" + branch, "--not", "--remotes=origin"], null, ct).ConfigureAwait(false);
        if (!ahead.Succeeded || !int.TryParse(ahead.Stdout.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int unpushed))
        {
            return "rev-list: " + (WorktreeRun.ErrorLine(ahead.Stderr) ?? "git rev-list failed");
        }

        if (unpushed > 0)
        {
            if (await git.ConfigGetAsync(project, "remote.origin.url", ct).ConfigureAwait(false) is null)
            {
                return "push: no origin";
            }

            var pushed = await git.PushBranchAsync(project, branch, ct).ConfigureAwait(false);
            if (!pushed.Succeeded)
            {
                return "push: " + (WorktreeRun.ErrorLine(pushed.Stderr) ?? "git push failed");
            }
        }

        var removed = await git.WorktreeRemoveAsync(project, folder, ct).ConfigureAwait(false);
        if (!removed.Succeeded)
        {
            return "remove: " + (WorktreeRun.ErrorLine(removed.Stderr) ?? "git worktree remove failed");
        }

        // Every commit of the branch is on the origin now (or was already): deleting the local branch loses nothing.
        await git.DeleteBranchAsync(project, branch, ct).ConfigureAwait(false);
        return null;
    }

    // The project a worktree belongs to, from its .git file (gitdir: <project>/.git/worktrees/<name>); null when that project is gone.
    private static string? ProjectOf(string folder)
    {
        try
        {
            string link = Path.Join(folder, ".git");
            if (!File.Exists(link))
            {
                return null;
            }

            string text = File.ReadAllText(link).Trim();
            if (!text.StartsWith("gitdir:", StringComparison.Ordinal))
            {
                return null;
            }

            string gitDir = Path.GetFullPath(text["gitdir:".Length..].Trim(), folder);
            string? common = Path.GetDirectoryName(Path.GetDirectoryName(gitDir));
            string? project = common is null ? null : Path.GetDirectoryName(common);
            return Directory.Exists(gitDir) && project is not null && Directory.Exists(project) ? project : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "leftover worktree of job {Id} kept at {Path}: {Why}")]
    private partial void LogKept(string id, string path, string why);

    [LoggerMessage(Level = LogLevel.Information, Message = "leftover worktree of job {Id} removed; its work is on the origin")]
    private partial void LogCleaned(string id);
}
