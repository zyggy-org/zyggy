using System.Text;

using Zyggy.Core.Memory;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Tests.Infrastructure;

/// <summary>
/// A temporary memory root with the principal <c>acme/alice</c>, written from an inline spec (relative path → content,
/// UTF-8 without BOM, bytes as given). Deleted on dispose.
/// </summary>
internal sealed class MemoryTree : IDisposable
{
    public static readonly Principal Alice = new(TenantId.Parse("acme"), UserId.Parse("alice"));

    public MemoryTree(params (string Path, string Content)[] files)
    {
        Root = Path.Combine(Path.GetTempPath(), "zyggy-ut", Guid.NewGuid().ToString("N"));
        PrincipalDirectory = Path.Combine(Root, "acme", "alice");
        Directory.CreateDirectory(PrincipalDirectory);
        foreach (var (path, content) in files)
        {
            Write(path, content);
        }
    }

    public string Root { get; }

    public string PrincipalDirectory { get; }

    public MemoryPaths Paths => new(Root, Alice);

    /// <summary>Copies a golden tree (<c>golden/&lt;relative&gt;/acme/alice</c>) into a fresh temporary root.</summary>
    public static MemoryTree CopyOf(string goldenRelative)
    {
        var tree = new MemoryTree();
        var source = Path.Combine(Golden.Directory, goldenRelative, "acme", "alice");
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(tree.PrincipalDirectory, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        return tree;
    }

    public string Full(string relative) => Path.Combine(PrincipalDirectory, relative.Replace('/', Path.DirectorySeparatorChar));

    public void Write(string relative, string content)
    {
        var full = Full(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new UTF8Encoding(false).GetBytes(content));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A leaked temp directory is harmless.
        }
    }
}
