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

    [Theory]
    [InlineData("login.microsoftonline.com", "src/Zyggy.Core/M365/Graph/GraphEndpoints.cs")]
    [InlineData("graph.microsoft.com", "src/Zyggy.Core/M365/Graph/GraphEndpoints.cs")]
    public void Src_ServiceLiteral_OnlyInItsAdapter(string literal, string allowed)
    {
        // Act
        var holders = SourceFiles().Where(f => File.ReadAllText(f).Contains(literal, StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetRelativePath(RepoRoot(), f).Replace('\\', '/'))
            .ToList();

        // Assert
        holders.Should().Equal(allowed);
    }

    [Fact]
    public void Src_ContainsNoMailboxSiteOrAccountLiteral()
    {
        // Act
        var offenders = SourceFiles().Where(f =>
        {
            var text = File.ReadAllText(f);
            return MailAddress().IsMatch(text) || text.Contains("sharepoint.com", StringComparison.OrdinalIgnoreCase) || GuidLiteral().IsMatch(text);
        }).Select(Path.GetFileName).ToList();

        // Assert
        offenders.Should().BeEmpty();
    }

    [Fact]
    public void Tests_NeverResolveRealExternalPrograms()
    {
        // Arrange: a ProcessSpec or ProcessStartInfo built with a bare program name would find the real one on PATH
        var tests = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "tests"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

        // Act
        var offenders = tests.Where(f => BareProgram().IsMatch(File.ReadAllText(f))).Select(Path.GetFileName).ToList();

        // Assert
        offenders.Should().BeEmpty();
    }

    [Fact]
    public void Src_McpSdk_OnlyUnderLinkedInMcp()
    {
        // Arrange: spec 36 Assumption 8 — the SDK is confined to the one server
        const string Allowed = "src/Zyggy.Core/LinkedIn/Mcp/";

        // Act
        var holders = SourceFiles().Where(f => McpSdk().IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(RepoRoot(), f).Replace('\\', '/'))
            .ToList();

        // Assert
        holders.Should().NotBeEmpty().And.OnlyContain(f => f.StartsWith(Allowed, StringComparison.Ordinal));
    }

    private static List<string> SourceFiles() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();

    [GeneratedRegex(@"\bDefault(Tenant|User|Principal)\b")]
    private static partial Regex DefaultTenant();

    [GeneratedRegex(@"""[^""\s]*[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+\.[A-Za-z]{2,}[^""\s]*""")]
    private static partial Regex MailAddress();

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")]
    private static partial Regex GuidLiteral();

    [GeneratedRegex(@"new (ProcessSpec|ProcessStartInfo)\(\s*""(claude|markitdown|ms-365-mcp-server)""")]
    private static partial Regex BareProgram();

    [GeneratedRegex(@"\bModelContextProtocol\.")]
    private static partial Regex McpSdk();
}
