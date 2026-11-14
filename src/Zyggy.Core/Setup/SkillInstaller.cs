using System.Security.Cryptography;

namespace Zyggy.Core.Setup;

/// <summary>
/// The skill items of <c>zyggy setup</c> (spec 42 AC-17, plan 42 A8): a node skill folder of the <c>zyggy-core</c> checkout is copied into the
/// user-scope skills directory. The same files with the same bytes leave the target alone; a differing target is first moved to the backup
/// folder outside the skills directory (so Claude Code never loads a second copy), then the source is copied. Nothing is deleted.
/// </summary>
internal static class SkillInstaller
{
    /// <summary>Installs one skill.</summary>
    /// <param name="item">The skill's item (<see cref="SetupItem.SkillRemember"/>, <see cref="SetupItem.SkillArchive"/>, <see cref="SetupItem.SkillBus"/>, <see cref="SetupItem.SkillDiscover"/>).</param>
    /// <param name="source">The skill in the checkout (<see cref="SetupPaths.SkillSource"/>).</param>
    /// <param name="target">The user-scope skill (<see cref="SetupPaths.SkillTarget"/>).</param>
    /// <param name="backup">Where a differing target is moved (<see cref="SetupPaths.SkillBackup"/>).</param>
    /// <param name="checkoutIfSkillMayBeAbsent">
    /// The checkout's folder, for a skill a <c>zyggy-core</c> checkout may not hold yet (spec 15: <c>bus</c>, <c>discover</c>): when that folder
    /// exists and the skill is not in it, the line is <see cref="SetupOutcome.LeftAsIs"/> and nothing is touched. <see langword="null"/> for a
    /// skill every checkout holds: its absence is a failure.
    /// </param>
    public static SetupLine Apply(SetupItem item, string source, string target, string backup, string? checkoutIfSkillMayBeAbsent = null)
    {
        if (!Directory.Exists(source))
        {
            return checkoutIfSkillMayBeAbsent is not null && Directory.Exists(checkoutIfSkillMayBeAbsent)
                ? new SetupLine(item, SetupOutcome.LeftAsIs, "not in this zyggy-core checkout")
                : new SetupLine(item, SetupOutcome.Failed, "zyggy-core not available");
        }

        try
        {
            if (!Directory.Exists(target))
            {
                CopyTree(source, target);
                return new SetupLine(item, SetupOutcome.Done, "copied from " + source);
            }

            if (SameBytes(source, target))
            {
                return new SetupLine(item, SetupOutcome.AlreadyDone, null);
            }

            MoveTree(target, backup);
            CopyTree(source, target);
            return new SetupLine(item, SetupOutcome.Done, "copied from " + source + " (backup " + backup + ")");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SetupLine(item, SetupOutcome.Failed, target + " could not be replaced: " + ex.Message);
        }
    }

    /// <summary>The same relative file set (empty folders ignored) and the same SHA-256 per file.</summary>
    internal static bool SameBytes(string a, string b)
    {
        Dictionary<string, string> left = Files(a);
        Dictionary<string, string> right = Files(b);
        return left.Count == right.Count
            && left.All(kv => right.TryGetValue(kv.Key, out string? other) && Hash(kv.Value).AsSpan().SequenceEqual(Hash(other)));
    }

    /// <summary>Every file under <paramref name="root"/>, keyed by its relative path with forward slashes.</summary>
    private static Dictionary<string, string> Files(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(root, f).Replace('\\', '/'), f => f, StringComparer.Ordinal);

    private static byte[] Hash(string file)
    {
        using FileStream stream = File.OpenRead(file);
        return SHA256.HashData(stream);
    }

    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Join(target, Path.GetRelativePath(source, directory)));
        }

        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Join(target, Path.GetRelativePath(source, file)), overwrite: false);
        }
    }

    /// <summary>Moves <paramref name="from"/> to <paramref name="to"/>; across volumes (another drive) it copies and then removes the copied source.</summary>
    private static void MoveTree(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(to))!);
        if (string.Equals(Path.GetPathRoot(Path.GetFullPath(from)), Path.GetPathRoot(Path.GetFullPath(to)), StringComparison.OrdinalIgnoreCase))
        {
            Directory.Move(from, to);
            return;
        }

        CopyTree(from, to);
        Directory.Delete(from, recursive: true);
    }
}
