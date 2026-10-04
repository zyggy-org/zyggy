namespace Zyggy.Core.Git;

/// <summary>One entry of <c>git status --porcelain=v1 -z</c>: the index and work-tree columns and the repository-relative path.</summary>
internal sealed record GitStatusEntry(char Index, char WorkTree, string Path)
{
    public bool Untracked => Index == '?';

    /// <summary>Changed in the work tree and not staged (or untracked): what a writer left behind without staging.</summary>
    public bool UnstagedOnly => Untracked || (Index == ' ' && WorkTree != ' ');
}

/// <summary>Parses <c>git status --porcelain=v1 -z</c> output.</summary>
internal static class GitStatus
{
    public static IReadOnlyList<GitStatusEntry> Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var entries = new List<GitStatusEntry>();
        var records = output.Split('\0');
        for (var i = 0; i < records.Length; i++)
        {
            var record = records[i];
            if (record.Length < 4)
            {
                continue;
            }

            entries.Add(new GitStatusEntry(record[0], record[1], record[3..]));
            if (record[0] is 'R' or 'C')
            {
                i++; // the rename or copy source follows as its own record
            }
        }

        return entries;
    }
}
