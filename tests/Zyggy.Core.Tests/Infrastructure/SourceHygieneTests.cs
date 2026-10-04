using System.Text.RegularExpressions;

namespace Zyggy.Core.Tests.Infrastructure;

/// <summary>AC-2: no tenant literal and no default tenant anywhere in <c>src/</c>.</summary>
public sealed partial class SourceHygieneTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Zyggy.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Zyggy.slnx not found above the test output.");
    }

    [Fact]
    public void Src_ContainsNoTenantLiteral()
    {
        // Arrange
        var files = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();

        // Act
        var offenders = files.Where(f =>
        {
            var text = File.ReadAllText(f);
            return text.Contains("geoffrey", StringComparison.OrdinalIgnoreCase) || DefaultTenant().IsMatch(text);
        }).ToList();

        // Assert
        files.Should().NotBeEmpty();
        offenders.Should().BeEmpty();
    }

    [GeneratedRegex(@"\bDefault(Tenant|User|Principal)\b")]
    private static partial Regex DefaultTenant();
}
