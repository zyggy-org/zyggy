using System.Runtime.Versioning;

using Zyggy.Core.M365;
using Zyggy.Core.M365.Mcp;
using Zyggy.Core.M365.Tools;

namespace Zyggy.Core.Tests.M365;

/// <summary>
/// The launch plan of the m365 MCP server — <c>mcp-server.sh</c> (spec 33 AC-25): the reviewed server under <c>~/.local</c>, exactly
/// its argv and the ten contracted variables (no token), the download root 0700.
/// </summary>
public sealed class McpServerLaunchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zyggy-ut", Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _environment;

    public McpServerLaunchTests()
    {
        Directory.CreateDirectory(Path.Combine(Home, ".local", "bin"));
        var tools = Path.Combine(Checkout, ".claude", "skills", "m365", "tools");
        Directory.CreateDirectory(tools);
        foreach (var file in Directory.EnumerateFiles(M365Run.Golden("tools")))
        {
            File.Copy(file, Path.Combine(tools, Path.GetFileName(file)));
        }

        Directory.CreateDirectory(Path.Combine(Checkout, "instance"));
        File.Copy(M365Run.Golden("fixtures", "m365.json"), Path.Combine(Checkout, "instance", "m365.json"));
        _environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["HOME"] = Home,
            ["PATH"] = Path.Combine(_root, "empty-path"),
            ["ZYGGY_INSTANCE_DIR"] = Path.Combine(Checkout, "instance"),
            ["ZYGGY_STATE_DIR"] = Path.Combine(_root, "state"),
        };
    }

    public static bool IsLinux => OperatingSystem.IsLinux();

    private string Home => Path.Combine(_root, "home");

    private string Checkout => Path.Combine(_root, "checkout");

    private string Server => Path.Combine(Home, ".local", "bin", "ms-365-mcp-server");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Plan_ArgvExact()
    {
        // Arrange
        InstallServer();

        // Act
        var plan = Plan().Plan!;

        // Assert
        plan.ServerPath.Should().Be(Server);
        plan.Argv.Should().Equal("--org-mode", "--http", "127.0.0.1:47365", "--http-local-file-tools", "--no-dynamic-registration");
    }

    [Fact]
    public void Plan_EnvironmentExactlyTenNamesNoToken()
    {
        // Arrange
        InstallServer();

        // Act
        var env = Plan().Plan!.Environment;

        // Assert
        env.Keys.Order(StringComparer.Ordinal).Should().Equal(
            "ENABLED_TOOLS", "HOME", "LC_ALL", "MS365_MCP_CLIENT_ID", "MS365_MCP_ORG_MODE", "MS365_MCP_TENANT_ID", "MS365_MCP_TOKEN_CACHE_PATH",
            "MS365_MCP_USE_KEYTAR", "NODE_OPTIONS", "PATH");
        env["PATH"].Should().Be($"/usr/bin:/bin:{Home}/.local/bin");
        env["HOME"].Should().Be(Home);
        env["LC_ALL"].Should().Be("C");
        env["NODE_OPTIONS"].Should().Be("--max-old-space-size=512");
        env["MS365_MCP_CLIENT_ID"].Should().Be("22222222-2222-4222-8222-222222222222");
        env["MS365_MCP_TENANT_ID"].Should().Be("11111111-1111-4111-8111-111111111111");
        env["MS365_MCP_ORG_MODE"].Should().Be("1");
        env["MS365_MCP_USE_KEYTAR"].Should().Be("0");
        env["MS365_MCP_TOKEN_CACHE_PATH"].Should().Be(Path.Join(_root, "state", "m365", "never-written.json"));
        env["ENABLED_TOOLS"].Should().StartWith("^(create-shared-mailbox-draft|").And.EndWith("|send-shared-mailbox-mail)$");
        env.Values.Should().NotContain(v => v.Contains("Bearer", StringComparison.Ordinal) || v.Contains("eyJ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null, "127.0.0.1:47365")]
    [InlineData("48000", "127.0.0.1:48000")]
    public void Plan_PortFromEnvDefault47365(string? port, string listen)
    {
        // Arrange
        InstallServer();
        _environment["ZYGGY_M365_PORT"] = port;

        // Assert
        Plan().Plan!.Argv[2].Should().Be(listen);
    }

    [Fact]
    public void Plan_PortOutOfRange_ExitThree()
    {
        // Arrange
        InstallServer();
        _environment["ZYGGY_M365_PORT"] = "80";

        // Act
        var plan = Plan();

        // Assert
        plan.Exit.Should().Be(3);
        plan.Error.Should().Be("configuration error: ZYGGY_M365_PORT '80' is not a port in 1024..65535");
    }

    [Fact]
    public void Plan_ServerNotFound_ExitThreeInstallHint()
    {
        // Act
        var plan = Plan();

        // Assert
        plan.Exit.Should().Be(3);
        plan.Error.Should().Be("ms-365-mcp-server not found — runbook 13 \"Install or upgrade the MCP server\"");
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "executable bits and symbolic links: Linux only")]
    [SupportedOSPlatform("linux")]
    public void Plan_ServerOutsideHomeLocal_ExitThree()
    {
        // Arrange: on PATH, but not under ~/.local; and a link from ~/.local/bin to it
        var elsewhere = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        var outside = Path.Combine(elsewhere, "ms-365-mcp-server");
        File.WriteAllText(outside, "#!/bin/sh\n");
        File.SetUnixFileMode(outside, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _environment["PATH"] = elsewhere;

        // Act
        var onPath = Plan();
        _environment["PATH"] = Path.Combine(_root, "empty-path");
        File.CreateSymbolicLink(Server, outside);
        var throughLink = Plan();

        // Assert
        foreach (var plan in new[] { onPath, throughLink })
        {
            plan.Exit.Should().Be(3);
            plan.Error.Should().Be($"ms-365-mcp-server at {outside} is not under {Home}/.local — runbook 13 \"Install or upgrade the MCP server\"");
        }
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "Unix file modes: Linux only")]
    [SupportedOSPlatform("linux")]
    public void EnsureDownloadRoot_CreatedAt0700()
    {
        // Act
        var error = McpServerLaunch.EnsureDownloadRoot(Path.Combine(Home, ".cache", "zyggy-m365-downloads"));

        // Assert
        error.Should().BeNull();
        File.GetUnixFileMode(Path.Combine(Home, ".cache", "zyggy-m365-downloads")).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "symbolic links: Linux only")]
    [SupportedOSPlatform("linux")]
    public void EnsureDownloadRoot_Symlink_ExitThree()
    {
        // Arrange
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(Path.Combine(Home, ".cache"));
        var root = Path.Combine(Home, ".cache", "zyggy-m365-downloads");
        Directory.CreateSymbolicLink(root, target);

        // Act
        var error = McpServerLaunch.EnsureDownloadRoot(root);

        // Assert
        error.Should().Be($"configuration error: the download root {root} is not a writable directory — runbook 13 \"MCP server down\"");
    }

    private void InstallServer()
    {
        File.WriteAllText(Server, "#!/bin/sh\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Server, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private LaunchPlanResult Plan()
    {
        var instance = M365Environment.Load(_environment).Environment!;
        var config = M365Configuration.Load(instance.ConfigPath, baseOnly: false, new DateOnly(2026, 9, 30), _ => true).Configuration!;
        var partition = M365ToolPartition.Load(instance.Checkout).Partition!;
        return McpServerLaunch.Plan(_environment, instance, config, partition);
    }
}
