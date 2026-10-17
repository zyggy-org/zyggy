using System.Text.RegularExpressions;

namespace Zyggy.Core.Tests.Infrastructure;

/// <summary>AC-2: no tenant literal and no default tenant anywhere in <c>src/</c>.</summary>
public sealed partial class SourceHygieneTests
{
    private static string RepoRoot() => Infrastructure.RepoRoot.Find();

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

    [Fact]
    public void Src_GraphWriteMethods_OnlyInDriveWriterAndTokenClient()
    {
        // Arrange: spec 38 — the token POST, the offload's two writes, and the loopback MCP probe (not Graph) are the only non-GET requests
        string[] allowed =
        [
            "src/Zyggy.Core/M365/Graph/GraphDriveWriter.cs",
            "src/Zyggy.Core/M365/Graph/GraphTokenClient.cs",
            "src/Zyggy.Core/M365/Mcp/McpProbe.cs",
        ];

        // Act
        var holders = SourceFiles()
            .Select(f => Path.GetRelativePath(RepoRoot(), f).Replace('\\', '/'))
            .Where(f => f.StartsWith("src/Zyggy.Core/M365/", StringComparison.Ordinal))
            .Where(f => WriteMethod().IsMatch(File.ReadAllText(Path.Combine(RepoRoot(), f))))
            .Order(StringComparer.Ordinal)
            .ToList();

        // Assert
        holders.Should().Equal(allowed);
    }

    [Fact]
    public void Src_GraphReader_GetOnly()
    {
        // Act
        var methods = Regex.Matches(File.ReadAllText(Path.Combine(RepoRoot(), "src", "Zyggy.Core", "M365", "Graph", "GraphReader.cs")), @"HttpMethod\.\w+")
            .Select(m => m.Value).Distinct().ToList();

        // Assert
        methods.Should().Equal("HttpMethod.Get");
    }

    [Fact]
    public void Src_MemoryAndMedia_NeverNameTextExtraction()
    {
        // Arrange: spec 39 AC-11/AC-13 — the archive never reads Office text; extraction stays in M365 (`zyggy m365 parse`).
        string[] forbidden = ["markitdown", "prlimit", "DocumentParser", "ParseVerb"];

        // Act
        var offenders = SourceFiles()
            .Select(f => Path.GetRelativePath(RepoRoot(), f).Replace('\\', '/'))
            .Where(f => f.StartsWith("src/Zyggy.Core/Memory/", StringComparison.Ordinal) || f.StartsWith("src/Zyggy.Core/Media/", StringComparison.Ordinal))
            .Where(f => forbidden.Any(word => File.ReadAllText(Path.Combine(RepoRoot(), f)).Contains(word, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // Assert
        offenders.Should().BeEmpty();
    }

    [Theory]
    [InlineData("src/Zyggy.Core/Memory/ArchiveChecks.cs")]
    [InlineData("src/Zyggy.Core/Media/MediaSniffer.cs")]
    [InlineData("src/Zyggy.Core/Media/OfficeSniffer.cs")]
    public void Src_ArchiveChecksAndMedia_NoProcessRunner(string file)
    {
        // Act: spec 39 AC-11 — `add` starts no process other than git, and the checks and sniffers start none at all.
        var text = File.ReadAllText(Path.Combine(RepoRoot(), file));

        // Assert
        text.Should().NotContainAny("IProcessRunner", "System.Diagnostics.Process");
    }

    [Theory]
    [InlineData("tenants/")]
    [InlineData("nodes/")]
    [InlineData("jobs/")]
    [InlineData("reports/")]
    [InlineData("context/")]
    [InlineData("archive/")]
    [InlineData("registry/")]
    [InlineData("PROTOCOL.md")]
    public void Src_BusPathLiterals_OnlyInBusPaths(string literal)
    {
        // Arrange: spec 04 AC-2 — a bus path is built in one place: the folder word in code (comments stripped), right after
        // an opening quote or a `/`. The memory repository has its own layout (`Memory/MemoryPaths`, used by the dream), so
        // `src/Zyggy.Core/Memory/` and `src/Zyggy.Core/Dream/` are outside this rule.
        const string Allowed = "src/Zyggy.Core/Bus/BusPaths.cs";
        var pattern = new Regex("[\"/]" + Regex.Escape(literal), RegexOptions.None, TimeSpan.FromSeconds(5));

        // Act
        var holders = SourceFiles()
            .Select(f => Path.GetRelativePath(RepoRoot(), f).Replace('\\', '/'))
            .Where(f => !f.StartsWith("src/Zyggy.Core/Memory/", StringComparison.Ordinal) && !f.StartsWith("src/Zyggy.Core/Dream/", StringComparison.Ordinal))
            .Where(f => pattern.IsMatch(CommentLine().Replace(File.ReadAllText(Path.Combine(RepoRoot(), f)), string.Empty)))
            .ToList();

        // Assert
        holders.Should().BeSubsetOf([Allowed]);
    }

    [Theory]
    [InlineData("api.github.com")]
    [InlineData("github.com")]
    [InlineData("x-ratelimit")]
    [InlineData("x-poll-interval")]
    public void Src_GitHubLiterals_OnlyInGitHubBusProvider(string literal)
    {
        // Arrange: spec 04 AC-2 — only the GitHub implementation of IBusProvider knows GitHub (the file arrives in Step 4)
        const string Allowed = "src/Zyggy.Core/Bus/GitHubBusProvider.cs";

        // Act
        var holders = SourceFiles()
            .Select(f => Path.GetRelativePath(RepoRoot(), f).Replace('\\', '/'))
            .Where(f => File.ReadAllText(Path.Combine(RepoRoot(), f)).Contains(literal, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Assert
        holders.Should().BeSubsetOf([Allowed]);
    }

    [Fact]
    public void Src_Bus_NeverDisablesProxy()
    {
        // Act: spec 04 AC-2/AC-11 — the bus honours the system proxy (§5 item 7); the product's UseProxy = false pattern must not leak in
        var offenders = BusSourceFiles().Where(f => DisabledProxy().IsMatch(File.ReadAllText(Path.Combine(RepoRoot(), f)))).ToList();

        // Assert
        offenders.Should().BeEmpty();
    }

    [Fact]
    public void Src_Bus_NoClockOrStaticState()
    {
        // Act: spec 04 AC-2 — the clock is injected (TimeProvider) and nothing under Bus/ or Configuration/ holds static mutable state
        var offenders = BusSourceFiles().Where(f =>
        {
            var text = File.ReadAllText(Path.Combine(RepoRoot(), f));
            return text.Contains("DateTime.UtcNow", StringComparison.Ordinal)
                || text.Contains("DateTimeOffset.UtcNow", StringComparison.Ordinal)
                || text.Contains("TimeProvider.System", StringComparison.Ordinal)
                || StaticMutableField().IsMatch(text);
        }).ToList();

        // Assert
        BusSourceFiles().Should().NotBeEmpty();
        offenders.Should().BeEmpty();
    }

    [Fact]
    public void Src_ClaudeProgramLiteral_OnlyInModelAdapterAndM365Preflight()
    {
        // Arrange: spec 06 AC-16 — "claude" as a program name lives in the model adapter; RunPreflight's is 33's (plan 06 A5), and
        // NodeConfigurationReader's is the node.json section key, not a program name (spec 06 Configuration).
        string[] allowed =
        [
            "src/Zyggy.Core/Configuration/NodeConfigurationReader.cs",
            "src/Zyggy.Core/M365/Runs/RunPreflight.cs",
            "src/Zyggy.Core/Models/ClaudeCodeCliRunner.cs",
            "src/Zyggy.Core/Models/ClaudeCodeOptions.cs",
        ];

        // Act
        var holders = CoreFilesContaining("\"claude\"");

        // Assert
        holders.Should().Contain("src/Zyggy.Core/Models/ClaudeCodeOptions.cs").And.BeSubsetOf(allowed);
    }

    [Fact]
    public void Src_ZyggyLockLiteral_OnlyInProjectLock()
    {
        // Act: spec 06 AC-16
        var holders = CoreFilesContaining("zyggy.lock");

        // Assert
        holders.Should().Equal("src/Zyggy.Core/Jobs/ProjectLock.cs");
    }

    [Fact]
    public void Src_RunsDirectoryLiteral_OnlyInTranscriptPaths()
    {
        // Act: spec 06 AC-16 — job transcripts' runs/ is built in one place. The brief and the dream keep their own run folders
        // under their state directories (deliverables 33/35 and 28), outside this rule like Memory/ and Dream/ are outside BusPaths'.
        var holders = CoreFilesContaining("\"runs\"")
            .Where(f => !f.StartsWith("src/Zyggy.Core/Brief/", StringComparison.Ordinal) && !f.StartsWith("src/Zyggy.Core/Dream/", StringComparison.Ordinal))
            .ToList();

        // Assert
        holders.Should().Equal("src/Zyggy.Core/Jobs/TranscriptPaths.cs");
    }

    [Fact]
    public void Src_ProcessId_OnlyInProjectLock()
    {
        // Act: spec 06 AC-16 (plan 06 A4: ProjectLock.CurrentProcessId is the one read)
        var holders = SourceFiles()
            .Where(f => File.ReadAllText(f).Contains("Environment.ProcessId", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(RepoRoot(), f).Replace('\\', '/'))
            .ToList();

        // Assert
        holders.Should().Equal("src/Zyggy.Core/Jobs/ProjectLock.cs");
    }

    [Fact]
    public void Src_JobsAndPolicy_NoClockOrStaticState()
    {
        // Act: spec 06 AC-16 — the clock is injected; only the DI extension may name TimeProvider.System (its TryAdd default)
        var offenders = JobsAndPolicySourceFiles().Where(f =>
        {
            var text = File.ReadAllText(Path.Combine(RepoRoot(), f));
            return text.Contains("DateTime.UtcNow", StringComparison.Ordinal)
                || text.Contains("DateTimeOffset.UtcNow", StringComparison.Ordinal)
                || (text.Contains("TimeProvider.System", StringComparison.Ordinal) && !f.EndsWith("/ServiceCollectionExtensions.cs", StringComparison.Ordinal))
                || StaticMutableField().IsMatch(text);
        }).ToList();

        // Assert
        JobsAndPolicySourceFiles().Should().NotBeEmpty();
        offenders.Should().BeEmpty();
    }

    [Theory]
    [InlineData("tenants/")]
    [InlineData("nodes/")]
    [InlineData("jobs/")]
    [InlineData("reports/")]
    [InlineData("context/")]
    [InlineData("archive/")]
    [InlineData("registry/")]
    [InlineData("PROTOCOL.md")]
    public void Src_Jobs_NoBusPathLiteral(string literal)
    {
        // Arrange: spec 06 AC-16 — the runner never touches the bus; the listener does
        var pattern = new Regex("[\"/]" + Regex.Escape(literal), RegexOptions.None, TimeSpan.FromSeconds(5));

        // Act
        var offenders = JobsAndPolicySourceFiles()
            .Where(f => f.StartsWith("src/Zyggy.Core/Jobs/", StringComparison.Ordinal))
            .Where(f => pattern.IsMatch(CommentLine().Replace(File.ReadAllText(Path.Combine(RepoRoot(), f)), string.Empty)))
            .ToList();

        // Assert
        offenders.Should().BeEmpty();
    }

    private static List<string> CoreFilesContaining(string literal) =>
        SourceFiles()
            .Select(f => Path.GetRelativePath(RepoRoot(), f).Replace('\\', '/'))
            .Where(f => f.StartsWith("src/Zyggy.Core/", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(Path.Combine(RepoRoot(), f)).Contains(literal, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

    private static List<string> JobsAndPolicySourceFiles() =>
        SourceFiles()
            .Select(f => Path.GetRelativePath(RepoRoot(), f).Replace('\\', '/'))
            .Where(f => f.StartsWith("src/Zyggy.Core/Jobs/", StringComparison.Ordinal) || f.StartsWith("src/Zyggy.Core/Policy/", StringComparison.Ordinal))
            .ToList();

    private static List<string> BusSourceFiles() =>
        SourceFiles()
            .Select(f => Path.GetRelativePath(RepoRoot(), f).Replace('\\', '/'))
            .Where(f => f.StartsWith("src/Zyggy.Core/Bus/", StringComparison.Ordinal) || f.StartsWith("src/Zyggy.Core/Configuration/", StringComparison.Ordinal))
            .ToList();

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

    [GeneratedRegex(@"HttpMethod\.(Put|Post|Patch|Delete)\b")]
    private static partial Regex WriteMethod();

    [GeneratedRegex(@"\bModelContextProtocol\.")]
    private static partial Regex McpSdk();

    [GeneratedRegex(@"UseProxy\s*=\s*false")]
    private static partial Regex DisabledProxy();

    [GeneratedRegex(@"^\s*//.*$", RegexOptions.Multiline)]
    private static partial Regex CommentLine();

    // A static field that is neither readonly nor const: `static Type name;` or `static Type name = …;` (methods have a `(`).
    [GeneratedRegex(@"^\s*(?:public|private|internal|protected)?\s*static\s+(?!readonly\b|const\b|class\b|partial\b|async\b)[^(=;{]*\s\w+\s*(?:=(?!>)|;)", RegexOptions.Multiline)]
    private static partial Regex StaticMutableField();
}
