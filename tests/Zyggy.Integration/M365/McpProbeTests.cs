using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.M365;

/// <summary>
/// Step 14 (spec 33 AC-26): <c>zyggy m365 mcp-server --probe</c> in process against a fake loopback server, the token minted through
/// the stubbed login host: the unauthenticated 401, then the tool list against the allowlist.
/// </summary>
public sealed class McpProbeTests : IAsyncDisposable
{
    private readonly M365InstanceFixture _fixture = new();
    private readonly StubGraphHandler _graph = StubGraphHandler.FromGoldenRoutes();
    private readonly TestCertificates _pair;
    private readonly FakeMcpServer _server;
    private readonly string[] _enabled;

    public McpProbeTests()
    {
        _pair = TestCertificates.Create(Path.Combine(_fixture.Home, ".config", "zyggy"));
        _enabled = File.ReadAllLines(M365InstanceFixture.Golden("m365", "tools", "enabled.txt"));
        _server = new FakeMcpServer(_enabled);
        InstallTools();
    }

    public async ValueTask DisposeAsync()
    {
        _graph.Violations.Should().BeEmpty();
        await _server.DisposeAsync();
        _pair.Dispose();
        _fixture.Dispose();
    }

    private Dictionary<string, string?> Env()
    {
        var env = _fixture.Env();
        env["ZYGGY_M365_PORT"] = _server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return env;
    }

    private void InstallTools()
    {
        var tools = Path.Combine(_fixture.Checkout, ".claude", "skills", "m365", "tools");
        Directory.CreateDirectory(tools);
        foreach (var file in Directory.EnumerateFiles(M365InstanceFixture.Golden("m365", "tools")))
        {
            File.Copy(file, Path.Combine(tools, Path.GetFileName(file)));
        }
    }

    private Task<(int Exit, VerbConsole Console)> Probe() => M365InProcess.RunAsync(Env(), _graph, ["mcp-server", "--probe"]);

    [Fact]
    public async Task Probe_401ThenToolsEqualAllowlist_PrintsToolsListenEnv()
    {
        // Act
        var (exit, console) = await Probe();

        // Assert
        exit.Should().Be(0, console.Stderr);
        var lines = console.Stdout.Split('\n');
        lines[0].Should().Be("tools: 16");
        lines[1..17].Should().Equal(_enabled);
        lines[17].Should().Be($"listen: 127.0.0.1:{_server.Port}");
        lines[18].Should().StartWith("env: ");
        _server.Methods.Should().Equal("initialize", "tools/list");
    }

    [Fact]
    public async Task Probe_BearerOnlyOnAuthenticatedRequests()
    {
        // Act
        await Probe();

        // Assert
        _server.BearerPresent.Should().Equal(false, true, true);
        _graph.Requests.Count(r => r.Method == HttpMethod.Post).Should().Be(2);
    }

    [Fact]
    public async Task Probe_OutputNeverContainsToken()
    {
        // Act
        var (_, console) = await Probe();

        // Assert
        (console.Stdout + console.Stderr).Should().NotContainAny("STUBACCESS", "Bearer");
    }

    [Fact]
    public async Task Probe_NoServer_ExitSixDownHint()
    {
        // Arrange
        var port = _server.Port;
        var env = Env();
        await _server.DisposeAsync();

        // Act
        var (exit, console) = await M365InProcess.RunAsync(env, _graph, ["mcp-server", "--probe"]);

        // Assert
        exit.Should().Be(6);
        console.Stderr.Should().StartWith($"m365: no server at 127.0.0.1:{port} (").And.EndWith(") — runbook 13 \"MCP server down\"\n");
    }

    [Fact]
    public async Task Probe_UnauthenticatedGets200_ExitSix()
    {
        // Arrange
        _server.UnauthenticatedStatus = 200;

        // Act
        var (exit, console) = await Probe();

        // Assert
        exit.Should().Be(6);
        console.Stderr.Should().Be("m365: an unauthenticated request got 200, not 401 — runbook 13 \"MCP server down\"\n");
    }

    [Fact]
    public async Task Probe_ExtraTool_ExitSixNamed()
    {
        // Arrange
        _server.Tools = [.. _enabled, "delete-mail-message", "send-mail"];

        // Act
        var (exit, console) = await Probe();

        // Assert
        exit.Should().Be(6);
        console.Stderr.Should().Be("m365: server offered tools outside ENABLED_TOOLS (delete-mail-message send-mail) — runbook 13 \"Install or upgrade the MCP server\"\n");
    }

    [Fact]
    public async Task Probe_MissingTools_ExitSixNamesUpToFive()
    {
        // Arrange
        _server.Tools = _enabled[7..];

        // Act
        var (exit, console) = await Probe();

        // Assert
        exit.Should().Be(6);
        console.Stderr.Should().Be($"m365: server did not offer {string.Join(' ', _enabled[..5])} — runbook 13 \"Install or upgrade the MCP server\"\n");
    }
}
