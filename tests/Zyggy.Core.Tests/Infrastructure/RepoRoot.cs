namespace Zyggy.Core.Tests.Infrastructure;

/// <summary>The repository root: the first directory above the test output that holds <c>Zyggy.slnx</c>.</summary>
internal static class RepoRoot
{
    public static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Zyggy.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Zyggy.slnx not found above the test output.");
    }
}
