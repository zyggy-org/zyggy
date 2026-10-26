using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Deploy;

/// <summary>The shipped deploy assets (spec 10 Contracts) read from the repository, and their expected copies next to the tests (plan 10 A1).</summary>
internal static class DeployAssets
{
    /// <summary>The owner-name needle of <c>SourceHygieneTests.Src_ContainsNoTenantLiteral</c>, duplicated here so a deploy asset is held to the same rule.</summary>
    public const string OwnerNeedle = "geoffrey";

    public static string Root => Path.Combine(RepoRoot.Find(), "deploy");

    public static string Text(string relativePath) => File.ReadAllText(PathOf(relativePath));

    public static byte[] Bytes(string relativePath) => File.ReadAllBytes(PathOf(relativePath));

    public static string Expected(string name) => File.ReadAllText(Path.Combine(RepoRoot.Find(), "tests", "Zyggy.Core.Tests", "Deploy", "Expected", name));

    public static byte[] ExpectedBytes(string name) => File.ReadAllBytes(Path.Combine(RepoRoot.Find(), "tests", "Zyggy.Core.Tests", "Deploy", "Expected", name));

    public static IReadOnlyList<string> Lines(string text) => text.Split('\n');

    public static IEnumerable<string> AllFiles() =>
        Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Root, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal);

    /// <summary>The installer's embedded <c>node.json</c> template: the text between the two marker lines, here-string delimiters stripped.</summary>
    public static string TemplateJson()
    {
        IReadOnlyList<string> lines = Lines(Text("windows/install.ps1"));
        int begin = lines.ToList().FindIndex(l => l.Contains("# node.json-template-begin", StringComparison.Ordinal));
        int end = lines.ToList().FindIndex(l => l.Contains("# node.json-template-end", StringComparison.Ordinal));
        if (begin < 0 || end < 0 || end <= begin)
        {
            throw new InvalidDataException("install.ps1 has no node.json template markers");
        }

        return string.Join("\n", lines.Skip(begin + 1).Take(end - begin - 1).Where(l => !l.TrimEnd().EndsWith("@'", StringComparison.Ordinal) && !l.TrimStart().StartsWith("'@", StringComparison.Ordinal))) + "\n";
    }

    /// <summary>The five replacements the script makes (plan 10 A3).</summary>
    public static string RenderTemplate(string tenant, string machine, string signWith, string busLocalPath, string userProfile) =>
        TemplateJson()
            .Replace("<tenant>", tenant, StringComparison.Ordinal)
            .Replace("<machine>", machine, StringComparison.Ordinal)
            .Replace("<signWith>", signWith, StringComparison.Ordinal)
            .Replace("<busLocalPath>", busLocalPath, StringComparison.Ordinal)
            .Replace("<userprofile>", userProfile, StringComparison.Ordinal);

    private static string PathOf(string relativePath) => Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
}
