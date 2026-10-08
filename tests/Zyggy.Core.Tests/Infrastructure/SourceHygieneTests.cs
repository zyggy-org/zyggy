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
    [InlineData("api.linkedin.com", "src/Zyggy.Core/LinkedIn/LinkedInEndpoints.cs")]
    [InlineData("www.linkedin.com", "src/Zyggy.Core/LinkedIn/LinkedInEndpoints.cs")]
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

    [Fact]
    public void Src_LinkedInVerbs_OnlyAuthAndMcpServer()
    {
        // Arrange: spec 36 AC-9 — no publishing verb on the command line; the only publish path is the consented MCP tool
        var host = new Zyggy.Core.LinkedIn.LinkedInVerbHost(new Dictionary<string, string?>());
        var table = typeof(Zyggy.Core.LinkedIn.LinkedInVerbHost)
            .GetField("_verbs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(host) as System.Collections.IDictionary;

        // Act
        var verbs = table!.Keys.Cast<string>().Order(StringComparer.Ordinal).ToList();

        // Assert
        verbs.Should().Equal("auth finish", "auth start", "auth status", "mcp-server");
    }

    [Fact]
    public void Src_LinkedInWrite_OnlyInCreatePost()
    {
        // Act: the Posts path is named once, and its route is used by one request — CreatePostAsync's
        var pathHolders = SourceFiles().Where(f => File.ReadAllText(f).Contains("\"/rest/posts\"", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(RepoRoot(), f).Replace('\\', '/')).ToList();
        var routeUsers = SourceFiles().Where(f => File.ReadAllText(f).Contains("routes.PostsUrl", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(RepoRoot(), f).Replace('\\', '/')).ToList();
        var api = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Zyggy.Core", "LinkedIn", "LinkedInApi.cs"));
        var createPost = api[api.IndexOf("public async Task<CreatePostResult> CreatePostAsync(", StringComparison.Ordinal)..];

        // Assert
        pathHolders.Should().Equal("src/Zyggy.Core/LinkedIn/LinkedInEndpoints.cs");
        routeUsers.Should().Equal("src/Zyggy.Core/LinkedIn/LinkedInApi.cs");
        Regex.Count(api, @"routes\.PostsUrl").Should().Be(1);
        createPost[..createPost.IndexOf("\n    }\n", StringComparison.Ordinal)].Should().Contain("routes.PostsUrl");
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
