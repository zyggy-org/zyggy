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
    public void Src_McpSdk_OnlyUnderMcpServers()
    {
        // Arrange: spec 36 Assumption 8 and plan 11 A10 — the SDK is confined to the two servers
        string[] allowed = ["src/Zyggy.Core/LinkedIn/Mcp/", "src/Zyggy.Core/Hub/"];

        // Act
        var holders = SourceFiles().Where(f => McpSdk().IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(RepoRoot(), f).Replace('\\', '/'))
            .ToList();

        // Assert
        holders.Should().NotBeEmpty().And.OnlyContain(f => allowed.Any(prefix => f.StartsWith(prefix, StringComparison.Ordinal)));
        holders.Should().Contain(f => f.StartsWith(allowed[1], StringComparison.Ordinal));
    }

    [Fact]
    public void Src_HubClasses_NoConsoleClockOrStaticState()
    {
        // Arrange: spec 11 — the Hub reads no clock of its own and holds no static mutable state; only the verb opens the console streams
        var texts = HubFiles().ToDictionary(f => f, f => CommentLine().Replace(File.ReadAllText(Path.Combine(RepoRoot(), f)), string.Empty), StringComparer.Ordinal);

        // Act
        var offenders = texts
            .Where(kv => (kv.Key.EndsWith("/HubVerb.cs", StringComparison.Ordinal)
                    ? ConsoleOtherThanStandardStreams().IsMatch(kv.Value)
                    : kv.Value.Contains("Console.", StringComparison.Ordinal))
                || kv.Value.Contains("DateTime.UtcNow", StringComparison.Ordinal)
                || kv.Value.Contains("DateTimeOffset.UtcNow", StringComparison.Ordinal)
                || kv.Value.Contains("TimeProvider.System", StringComparison.Ordinal)
                || StaticMutableField().IsMatch(kv.Value))
            .Select(kv => kv.Key)
            .ToList();

        // Assert
        texts.Should().HaveCountGreaterThanOrEqualTo(4);
        offenders.Should().BeEmpty();
    }

    [Fact]
    public void Src_RawVerbs_DispatchHub()
    {
        // Arrange: spec 11 D5 — the Hub is the zyggy hub verb, run with its own argument check, never parsed by System.CommandLine
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Zyggy.Cli", "RawVerbs.cs"));

        // Assert
        text.Should().Contain("""["hub", .. var hub]""").And.Contain("new HubVerb(");
    }

    [Fact]
    public void Src_Hub_NoPathBuilding()
    {
        // Arrange: spec 11 — MemoryStore (in Memory/) is the Hub's one reader and RememberService its one writer; the Hub builds no path
        var texts = HubFiles().ToDictionary(f => f, f => CommentLine().Replace(File.ReadAllText(Path.Combine(RepoRoot(), f)), string.Empty), StringComparer.Ordinal);

        // Act
        var offenders = texts.Where(kv => FileSystemAccess().IsMatch(kv.Value)).Select(kv => kv.Key).ToList();

        // Assert
        texts.Should().NotBeEmpty();
        offenders.Should().BeEmpty();
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

    [Fact]
    public void Packages_Deliverable07_PinnedExactly()
    {
        // Arrange: spec 07 Dependencies — exact versions, nothing floating, no telemetry package (O8)
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Microsoft.Extensions.Hosting.WindowsServices"] = "10.0.12",
            ["Microsoft.Extensions.Hosting.Systemd"] = "10.0.12",
            ["Serilog"] = "4.4.0",
            ["Serilog.Extensions.Hosting"] = "10.0.0",
            ["Serilog.Sinks.File"] = "7.0.0",
            ["Serilog.Sinks.Console"] = "6.1.1",
            ["TimeZoneConverter"] = "8.0.0",
        };
        var document = System.Xml.Linq.XDocument.Load(Path.Combine(RepoRoot(), "Directory.Packages.props"));

        // Act
        var pinned = document.Descendants("PackageVersion")
            .ToDictionary(e => (string)e.Attribute("Include")!, e => (string)e.Attribute("Version")!, StringComparer.Ordinal);

        // Assert
        foreach (var (package, version) in expected)
        {
            pinned.Should().ContainKey(package).WhoseValue.Should().Be(version);
        }

        pinned.Keys.Should().NotContain(k => k.Contains("OpenTelemetry", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Src_Node_NoAppSettings()
    {
        // Arrange: spec 07 ex-Q6 — node.json is the single configuration file
        var node = Path.Combine(RepoRoot(), "src", "Zyggy.Node");

        // Act
        var offenders = Directory.EnumerateFiles(node, "appsettings*.json", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();

        // Assert
        offenders.Should().BeEmpty();
    }

    [Fact]
    public void Src_Node_KestrelFrameworkReferencedOnce()
    {
        // Arrange: spec 07 ex-Q1 — the health endpoint's framework reference lives in the Node project only
        var projects = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.csproj", SearchOption.AllDirectories);

        // Act
        var holders = projects
            .Where(f => File.ReadAllText(f).Contains("Microsoft.AspNetCore.App", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(RepoRoot(), f).Replace('\\', '/'))
            .ToList();

        // Assert
        holders.Should().Equal("src/Zyggy.Node/Zyggy.Node.csproj");
    }

    [Fact]
    public void Src_Node_KestrelListensLocalhostOnly()
    {
        // Arrange: spec 07 AC-12 — the health endpoint binds loopback only; nothing may widen it
        var node = Path.Combine(RepoRoot(), "src", "Zyggy.Node");
        var files = Directory.EnumerateFiles(node, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToDictionary(f => Path.GetRelativePath(RepoRoot(), f).Replace('\\', '/'), File.ReadAllText, StringComparer.Ordinal);
        string[] forbidden = ["ListenAnyIP", "UseUrls", "ASPNETCORE_URLS", "\"0.0.0.0\"", "\"*\""];

        // Act
        var listeners = files.Where(kv => kv.Value.Contains("ListenLocalhost", StringComparison.Ordinal)).Select(kv => kv.Key).ToList();
        var offenders = files.Where(kv => forbidden.Any(t => kv.Value.Contains(t, StringComparison.Ordinal))).Select(kv => kv.Key).ToList();

        // Assert
        listeners.Should().Equal("src/Zyggy.Node/HealthServer.cs");
        offenders.Should().BeEmpty();
    }

    [Fact]
    public void Src_Node_NoClockOrStaticStateOutsideNodeHost()
    {
        // Arrange: spec 07 AC-12 — the clock comes from the composition root; no static mutable state
        var offenders = NodeSourceFiles()
            .Where(kv => kv.Value.Contains("DateTime.UtcNow", StringComparison.Ordinal)
                || kv.Value.Contains("DateTimeOffset.UtcNow", StringComparison.Ordinal)
                || kv.Value.Contains("TimeProvider.System", StringComparison.Ordinal)
                || StaticMutableField().IsMatch(CommentLine().Replace(kv.Value, string.Empty)))
            .Select(kv => kv.Key)
            .ToList();

        // Assert
        offenders.Should().BeEmpty();
    }

    [Fact]
    public void Src_Node_NoProcessHttpClientGitHubClaudeOrSecretStoreReference()
    {
        // Arrange: spec 07 AC-12 — the Node composes Core; every seam implementation stays in Core
        string[] forbidden = ["System.Diagnostics.Process", "new HttpClient", "HttpClient", "github", "GitHub", "\"claude\"", "FileSecretStore", "ISecretStore"];

        // Act
        var offenders = NodeSourceFiles()
            .Where(kv => forbidden.Any(t => CommentLine().Replace(kv.Value, string.Empty).Contains(t, StringComparison.Ordinal)))
            .Select(kv => kv.Key)
            .ToList();

        // Assert
        offenders.Should().BeEmpty();
    }

    [Fact]
    public void Src_Node_NoBusPathLiteral()
    {
        // Arrange: spec 07 AC-12 — bus paths only through BusPaths (in Core)
        string[] literals = ["tenants/", "nodes/", "jobs/", "reports/", "context/", "archive/", "registry/", "PROTOCOL.md"];

        // Act
        var offenders = NodeSourceFiles()
            .Where(kv => literals.Any(t => CommentLine().Replace(kv.Value, string.Empty).Contains("\"" + t, StringComparison.Ordinal)))
            .Select(kv => kv.Key)
            .ToList();

        // Assert
        offenders.Should().BeEmpty();
    }

    [Fact]
    public void Src_NoDangerouslySkipPermissions()
    {
        // Act: spec 06 AC-16 re-asserted over all of src/ (spec 07 AC-12)
        var offenders = SourceFiles()
            .Where(f => File.ReadAllText(f).Contains("dangerously-skip-permissions", StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetRelativePath(RepoRoot(), f).Replace('\\', '/'))
            .ToList();

        // Assert
        offenders.Should().BeEmpty();
    }

    private static Dictionary<string, string> NodeSourceFiles() =>
        SourceFiles()
            .Select(f => Path.GetRelativePath(RepoRoot(), f).Replace('\\', '/'))
            .Where(f => f.StartsWith("src/Zyggy.Node/", StringComparison.Ordinal))
            .ToDictionary(f => f, f => File.ReadAllText(Path.Combine(RepoRoot(), f)), StringComparer.Ordinal);

    [Fact]
    public void Src_Cli_ReferencesCoreOnly()
    {
        // Arrange: spec 08 AC-6 — the CLI stays slim: one project reference, no framework reference (the Node's Kestrel never enters it)
        var project = System.Xml.Linq.XDocument.Load(Path.Combine(RepoRoot(), "src", "Zyggy.Cli", "Zyggy.Cli.csproj"));

        // Act
        var references = project.Descendants("ProjectReference").Select(e => (string)e.Attribute("Include")!).ToList();
        var frameworks = project.Descendants("FrameworkReference").ToList();

        // Assert
        references.Should().ContainSingle().Which.Should().EndWith("Zyggy.Core.csproj");
        frameworks.Should().BeEmpty();
    }

    [Fact]
    public void Src_Cli_NoProcessHttpClientGitHubClaudeOrSecretStoreReference()
    {
        // Arrange: spec 08 AC-6 — the bus verbs compose Core; every seam implementation stays in Core. The composition root may name the
        // bus implementation's options record for the deliverable-04 test hook (ZYGGY_BUS_API_BASE) and nothing else of it. The two
        // pre-bus verbs are out of this fact's scope by design: `secret` writes through the ISecretStore seam (its whole purpose) and
        // `dream` carries the model path option — neither touches the bus.
        string[] forbidden = ["System.Diagnostics.Process", "HttpClient", "github", "GitHub", "\"claude\"", "FileSecretStore", "ISecretStore"];
        string[] preBusVerbs = ["src/Zyggy.Cli/Commands/SecretCommand.cs", "src/Zyggy.Cli/Commands/DreamCommand.cs"];

        // Act
        var offenders = CliSourceFiles()
            .Where(kv => !preBusVerbs.Contains(kv.Key, StringComparer.Ordinal))
            .Select(kv => (kv.Key, Text: CommentLine().Replace(kv.Value, string.Empty).Replace("GitHubBusOptions", string.Empty, StringComparison.Ordinal)))
            .Where(kv => forbidden.Any(t => kv.Text.Contains(t, StringComparison.Ordinal)))
            .Select(kv => kv.Key)
            .ToList();

        // Assert
        offenders.Should().BeEmpty();
    }

    [Fact]
    public void Src_Deliverable08Classes_NoConsoleClockOrStaticState()
    {
        // Arrange: spec 08 AC-16 — the new Core classes read no console, no clock of their own, hold no static mutable state
        string[] files =
        [
            "src/Zyggy.Core/Bus/JobRequest.cs", "src/Zyggy.Core/Bus/ProjectReference.cs", "src/Zyggy.Core/Bus/JobSubmitter.cs",
            "src/Zyggy.Core/Bus/BusInventory.cs", "src/Zyggy.Core/Bus/BusAuditor.cs",
        ];
        var texts = files.Concat(SourceFiles().Select(f => Path.GetRelativePath(RepoRoot(), f).Replace('\\', '/')).Where(f => f.StartsWith("src/Zyggy.Core/Health/", StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(f => f, f => CommentLine().Replace(File.ReadAllText(Path.Combine(RepoRoot(), f)), string.Empty), StringComparer.Ordinal);

        // Act
        var offenders = texts
            .Where(kv => kv.Value.Contains("Console.", StringComparison.Ordinal)
                || kv.Value.Contains("DateTime.UtcNow", StringComparison.Ordinal)
                || kv.Value.Contains("DateTimeOffset.UtcNow", StringComparison.Ordinal)
                || kv.Value.Contains("TimeProvider.System", StringComparison.Ordinal)
                || StaticMutableField().IsMatch(kv.Value))
            .Select(kv => kv.Key)
            .ToList();

        // Assert
        texts.Should().HaveCountGreaterThanOrEqualTo(8);
        offenders.Should().BeEmpty();
    }

    [Fact]
    public void Src_UseProxyFalse_OnlyInHealthClient()
    {
        // Act: spec 08 — the loopback health client is the one place the proxy is bypassed (a corporate proxy must never see localhost)
        var holders = SourceFiles()
            .Select(f => Path.GetRelativePath(RepoRoot(), f).Replace('\\', '/'))
            .Where(f => f.StartsWith("src/Zyggy.Core/Bus/", StringComparison.Ordinal) || f.StartsWith("src/Zyggy.Core/Configuration/", StringComparison.Ordinal)
                || f.StartsWith("src/Zyggy.Core/Health/", StringComparison.Ordinal) || f.StartsWith("src/Zyggy.Cli/", StringComparison.Ordinal))
            .Where(f => DisabledProxy().IsMatch(File.ReadAllText(Path.Combine(RepoRoot(), f))))
            .ToList();

        // Assert
        holders.Should().Equal("src/Zyggy.Core/Health/HealthClient.cs");
    }

    [Fact]
    public void Src_HealthClient_LoopbackGuard()
    {
        // Assert: the guard is in the constructor, before any request exists
        File.ReadAllText(Path.Combine(RepoRoot(), "src", "Zyggy.Core", "Health", "HealthClient.cs")).Should().Contain("IsLoopback");
    }

    [Fact]
    public void Packages_RowCountPinned()
    {
        // Arrange: spec 08 AC-15 / spec 09 — 23 rows at 08's gate, one Credential Manager row in 09; no HTTP client package ever enters
        var document = System.Xml.Linq.XDocument.Load(Path.Combine(RepoRoot(), "Directory.Packages.props"));

        // Act
        var rows = document.Descendants("PackageVersion").Select(e => (string)e.Attribute("Include")!).ToList();

        // Assert
        rows.Should().HaveCount(24);
        rows.Should().NotContain(r => r.Contains("Http", StringComparison.OrdinalIgnoreCase) || r.Contains("Refit", StringComparison.OrdinalIgnoreCase) || r.Contains("RestSharp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Packages_Deliverable09_CredentialManagerPinnedExactly()
    {
        // Arrange: spec 09 — the one new package, exact version; no DPAPI, D-Bus or libsecret package
        var document = System.Xml.Linq.XDocument.Load(Path.Combine(RepoRoot(), "Directory.Packages.props"));

        // Act
        var rows = document.Descendants("PackageVersion").ToDictionary(e => (string)e.Attribute("Include")!, e => (string)e.Attribute("Version")!, StringComparer.Ordinal);

        // Assert
        rows.Should().ContainKey("Meziantou.Framework.Win32.CredentialManager").WhoseValue.Should().Be("3.0.6");
        rows.Keys.Should().NotContain(k => k.Contains("ProtectedData", StringComparison.OrdinalIgnoreCase) || k.Contains("Tmds.DBus", StringComparison.OrdinalIgnoreCase) || k.Contains("libsecret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Src_CredentialManagerPackage_OnlyUnderSecretsWindows()
    {
        // Act: spec 09 AC-13 — the package is named only by the Windows store's folder and the Core project file
        var holders = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".csproj", StringComparison.Ordinal))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("Meziantou", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(RepoRoot(), f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();

        // Assert
        holders.Should().NotBeEmpty();
        holders.Should().OnlyContain(f => f.StartsWith("src/Zyggy.Core/Secrets/Windows/", StringComparison.Ordinal) || f == "src/Zyggy.Core/Zyggy.Core.csproj");
    }

    [Fact]
    public void Src_NoRawCredentialPInvoke()
    {
        // Act: spec 09 — the Credential Manager is reached through the package, never through a raw P/Invoke
        string[] forbidden = ["CredRead", "CredWrite", "CredDelete", "CredEnumerate", "advapi32"];
        var offenders = SourceFiles()
            .Where(f => forbidden.Any(t => CommentLine().Replace(File.ReadAllText(f), string.Empty).Contains(t, StringComparison.Ordinal)))
            .Select(f => Path.GetRelativePath(RepoRoot(), f).Replace('\\', '/'))
            .ToList();

        // Assert
        offenders.Should().BeEmpty();
    }

    [Fact]
    public void Src_HealthUrlOverride_TestHookOnly()
    {
        // Act: spec 08 Configuration — ZYGGY_HEALTH_URL is a loopback-only test hook read by the status verb, never a node.json key
        var holders = SourceFiles()
            .Where(f => File.ReadAllText(f).Contains("ZYGGY_HEALTH_URL", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(RepoRoot(), f).Replace('\\', '/'))
            .ToList();

        // Assert
        holders.Should().Equal("src/Zyggy.Cli/Commands/StatusCommand.cs");
    }

    private static Dictionary<string, string> CliSourceFiles() =>
        SourceFiles()
            .Select(f => Path.GetRelativePath(RepoRoot(), f).Replace('\\', '/'))
            .Where(f => f.StartsWith("src/Zyggy.Cli/", StringComparison.Ordinal))
            .ToDictionary(f => f, f => File.ReadAllText(Path.Combine(RepoRoot(), f)), StringComparer.Ordinal);

    [Fact]
    public void Src_Deliverable09Classes_NoConsoleClockOrStaticState()
    {
        // Arrange: spec 09 AC-13 — the new Core classes read no console, no clock of their own, hold no static mutable state
        string[] named = ["src/Zyggy.Core/Secrets/LinuxSecretStore.cs", "src/Zyggy.Core/Secrets/SecretFingerprint.cs", "src/Zyggy.Core/Secrets/SecretStoreUnavailableException.cs"];
        var texts = SourceFiles()
            .Select(f => Path.GetRelativePath(RepoRoot(), f).Replace('\\', '/'))
            .Where(f => f.StartsWith("src/Zyggy.Core/Secrets/Verbs/", StringComparison.Ordinal) || f.StartsWith("src/Zyggy.Core/Secrets/Windows/", StringComparison.Ordinal))
            .Concat(named)
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(f => f, f => CommentLine().Replace(File.ReadAllText(Path.Combine(RepoRoot(), f)), string.Empty), StringComparer.Ordinal);

        // Act
        var offenders = texts
            .Where(kv => kv.Value.Contains("Console.", StringComparison.Ordinal)
                || kv.Value.Contains("DateTime.UtcNow", StringComparison.Ordinal)
                || kv.Value.Contains("DateTimeOffset.UtcNow", StringComparison.Ordinal)
                || kv.Value.Contains("TimeProvider.System", StringComparison.Ordinal)
                || StaticMutableField().IsMatch(kv.Value))
            .Select(kv => kv.Key)
            .ToList();

        // Assert
        texts.Should().HaveCountGreaterThanOrEqualTo(9);
        offenders.Should().BeEmpty();
    }

    [Fact]
    public void Src_CredentialsDirectoryLiteral_OnlyInKnownReaders()
    {
        // Arrange: plan 09 A14 — the systemd variable is read by these files and no other; a new holder is a design change
        string[] allowed =
        [
            "src/Zyggy.Core/Secrets/CredentialFileSecretStore.cs", "src/Zyggy.Core/Secrets/CredentialSource.cs", "src/Zyggy.Core/Secrets/LinuxSecretStore.cs",
            "src/Zyggy.Core/Models/ClaudeCodeCliRunner.cs", "src/Zyggy.Core/Models/ModelRunRequest.cs", "src/Zyggy.Core/M365/M365Paths.cs",
            "src/Zyggy.Core/M365/Runs/M365RunRequest.cs", "src/Zyggy.Core/LinkedIn/LinkedInPaths.cs", "src/Zyggy.Core/Memory/SourceDenyList.cs",
        ];

        // Act
        var holders = SourceFiles()
            .Where(f => CommentLine().Replace(File.ReadAllText(f), string.Empty).Contains("CREDENTIALS_DIRECTORY", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(RepoRoot(), f).Replace('\\', '/'))
            .ToList();

        // Assert
        holders.Should().Contain("src/Zyggy.Core/Secrets/LinuxSecretStore.cs");
        holders.Should().OnlyContain(f => allowed.Contains(f));
    }

    [Fact]
    public void Src_SecretVerbs_NeverWriteArgv()
    {
        // Act: spec 09 AC-7 — the only arguments of the secret verbs are the two names (set, remove); a value never has a form on argv
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Zyggy.Cli", "Commands", "SecretCommand.cs"));

        // Assert
        Regex.Matches(source, @"new Argument<").Should().HaveCount(2);
        source.Should().NotContain("Argument<byte");
    }

    private static readonly string[] ProductReadmes = ["src/Zyggy.Cli/README.md", "src/Zyggy.Core/README.md", "src/Zyggy.Node/README.md", "src/Zyggy.Hub/README.md"];

    [Fact]
    public void Readmes_HubDocumented()
    {
        // Arrange: spec 11 Step 7 — the verb row, the two runbook entries, the Hub README served by `zyggy hub`, the Core folder row
        static string Read(string relative) => File.ReadAllText(Path.Combine(RepoRoot(), relative));
        var cli = Read("src/Zyggy.Cli/README.md");
        var hub = Read("src/Zyggy.Hub/README.md");
        var core = Read("src/Zyggy.Core/README.md");

        // Act + Assert
        cli.Should().Contain("Hub fails to start").And.Contain("Hub refuses a principal").And.Contain("| `hub`");
        hub.Should().Contain("zyggy hub").And.NotContain("zyggy-hub");
        core.Should().Contain("Hub/");
    }

    [Fact]
    public void HubTestsAndFixtures_NonDefaultPrincipalOnly()
    {
        // Arrange: spec 11 AC-25 — the Hub's sources, tests and goldens are public: placeholders and the acme/alice principal only
        string[] directories = ["tests/golden/hub", "tests/Zyggy.Core.Tests/Hub", "tests/Zyggy.Integration/Hub", "src/Zyggy.Core/Hub"];
        var files = directories
            .SelectMany(d => Directory.EnumerateFiles(Path.Combine(RepoRoot(), d), "*", SearchOption.AllDirectories))
            .ToDictionary(f => Path.GetRelativePath(RepoRoot(), f).Replace('\\', '/'), File.ReadAllText, StringComparer.Ordinal);

        // Act
        var offenders = files.Where(kv =>
                kv.Value.Contains("geoffrey", StringComparison.OrdinalIgnoreCase)
                || MailAddress().IsMatch(kv.Value)
                || GuidLiteral().IsMatch(kv.Value)
                || kv.Value.Contains("/home/", StringComparison.Ordinal)
                || kv.Value.Contains("/srv/", StringComparison.Ordinal)
                || kv.Value.Contains("C:\\Users\\", StringComparison.Ordinal))
            .Select(kv => kv.Key)
            .ToList();

        // Assert
        files.Should().HaveCountGreaterThanOrEqualTo(18);
        offenders.Should().BeEmpty();
        files.Values.Should().Contain(text => text.Contains("acme", StringComparison.Ordinal))
            .And.Contain(text => text.Contains("alice", StringComparison.Ordinal));
    }

    [Fact]
    public void Deploy_AndReadmes_NoRealIdentifierNoAbsoluteHomeNoSkipPermissions()
    {
        // Arrange: spec 10 AC-6 — the shipped deploy assets and the three product READMEs are public: placeholders only
        var files = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "deploy"), "*", SearchOption.AllDirectories)
            .Concat(ProductReadmes.Select(f => Path.Combine(RepoRoot(), f)))
            .ToDictionary(f => Path.GetRelativePath(RepoRoot(), f).Replace('\\', '/'), File.ReadAllText, StringComparer.Ordinal);

        // Act
        var offenders = files.Where(kv =>
                kv.Value.Contains("geoffrey", StringComparison.OrdinalIgnoreCase)
                || MailAddress().IsMatch(kv.Value)
                || GuidLiteral().IsMatch(kv.Value)
                || kv.Value.Contains("/home/", StringComparison.Ordinal)
                || kv.Value.Contains("/srv/", StringComparison.Ordinal)
                || kv.Value.Contains("C:\\Users\\", StringComparison.Ordinal)
                || kv.Value.Contains("sharepoint.com", StringComparison.OrdinalIgnoreCase)
                || kv.Value.Split('\n').Any(l => l.Contains("dangerously-skip-permissions", StringComparison.Ordinal) && !l.Contains("never", StringComparison.Ordinal)))
            .Select(kv => kv.Key)
            .ToList();

        // Assert
        files.Should().HaveCountGreaterThanOrEqualTo(7);
        offenders.Should().BeEmpty();
        SecretSweep.AssertAbsent(files.Values);
    }

    [Fact]
    public void Readmes_NoWindowsServiceInstallWording()
    {
        // Act: spec 10 (owner 2026-10-26) — there is no Windows service; the laptops run a per-user logon task
        var offenders = ProductReadmes
            .SelectMany(f => File.ReadAllLines(Path.Combine(RepoRoot(), f)).Select((line, i) => (File: f, Line: i + 1, Text: line)))
            .Where(l => l.Text.Contains("Windows service", StringComparison.Ordinal)
                && (l.Text.Contains("install", StringComparison.OrdinalIgnoreCase) || l.Text.Contains("deliverable 10", StringComparison.Ordinal) || l.Text.Contains("register", StringComparison.OrdinalIgnoreCase)))
            .Select(l => $"{l.File}:{l.Line}")
            .ToList();

        // Assert
        offenders.Should().BeEmpty();
    }

    [Fact]
    public void Src_NoOpenTelemetryNamespace()
    {
        // Arrange: spec 07 / O8 — telemetry is off in v1; no collector wired anywhere
        var files = SourceFiles()
            .Concat(Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.csproj", SearchOption.AllDirectories));

        // Act
        var offenders = files
            .Where(f => File.ReadAllText(f).Contains("OpenTelemetry", StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetRelativePath(RepoRoot(), f).Replace('\\', '/'))
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

    [GeneratedRegex(@"new (ProcessSpec|ProcessStartInfo)\(\s*""(claude|markitdown|ms-365-mcp-server|cmdkey)""")]
    private static partial Regex BareProgram();

    [GeneratedRegex(@"HttpMethod\.(Put|Post|Patch|Delete)\b")]
    private static partial Regex WriteMethod();

    [GeneratedRegex(@"\bModelContextProtocol\.")]
    private static partial Regex McpSdk();

    [GeneratedRegex(@"UseProxy\s*=\s*false")]
    private static partial Regex DisabledProxy();

    // Console members other than the two standard streams the Hub verb hands to the server.
    [GeneratedRegex(@"\bConsole\.(?!OpenStandardInput\b|OpenStandardOutput\b)")]
    private static partial Regex ConsoleOtherThanStandardStreams();

    // A static call on the file-system types (not a member access such as `entry.File.Name`).
    [GeneratedRegex(@"(?<![\w.])(?:File|Directory|Path)\.(?:Join|Combine|\w+)\b")]
    private static partial Regex FileSystemAccess();

    private static IEnumerable<string> HubFiles() =>
        SourceFiles().Select(f => Path.GetRelativePath(RepoRoot(), f).Replace('\\', '/'))
            .Where(f => f.StartsWith("src/Zyggy.Core/Hub/", StringComparison.Ordinal));

    [GeneratedRegex(@"^\s*//.*$", RegexOptions.Multiline)]
    private static partial Regex CommentLine();

    // A static field that is neither readonly nor const: `static Type name;` or `static Type name = …;` (methods have a `(`).
    [GeneratedRegex(@"^\s*(?:public|private|internal|protected)?\s*static\s+(?!readonly\b|const\b|class\b|partial\b|async\b)[^(=;{]*\s\w+\s*(?:=(?!>)|;)", RegexOptions.Multiline)]
    private static partial Regex StaticMutableField();
}
