using System.Diagnostics;
using System.Runtime.Versioning;

using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.M365;

/// <summary>
/// Step 14 (spec 33 AC-25): <c>zyggy m365 mcp-server</c> from the built binary becomes the (fake) server through <c>execve</c>: exactly its
/// argv and the ten variables, no token; a SIGTERM reaches it and its exit code comes back.
/// </summary>
public sealed class McpServerCommandTests : IDisposable
{
    private readonly M365InstanceFixture _fixture = new();

    public McpServerCommandTests()
    {
        var tools = Path.Combine(_fixture.Checkout, ".claude", "skills", "m365", "tools");
        Directory.CreateDirectory(tools);
        foreach (var file in Directory.EnumerateFiles(M365InstanceFixture.Golden("m365", "tools")))
        {
            File.Copy(file, Path.Combine(tools, Path.GetFileName(file)));
        }
    }

    public static bool IsLinux => OperatingSystem.IsLinux();

    public static bool IsNotLinux => !OperatingSystem.IsLinux();

    private string Server => Path.Combine(_fixture.UserBin, "ms-365-mcp-server");

    private string Log => Path.Combine(_fixture.Root, "server.log");

    public void Dispose() => _fixture.Dispose();

    private Task<ZyggyRun> McpServer(Dictionary<string, string?>? env = null, params string[] args) =>
        ZyggyCli.RunAsync(["m365", "mcp-server", .. args], env ?? _fixture.Env(), null, _fixture.Root, TestContext.Current.CancellationToken);

    // The fake server logs its argv, its variable names and whether any variable holds a bearer token, then exits 0.
    [SupportedOSPlatform("linux")]
    private void InstallLoggingServer() => M365InstanceFixture.WriteScript(Server, $$"""
        #!/bin/sh
        printf 'argv=%s\n' "$*" > '{{Log}}'
        env | cut -d= -f1 | sort | tr '\n' ' ' | sed 's/^/names=/' >> '{{Log}}'
        printf '\n' >> '{{Log}}'
        if env | grep -q 'eyJ'; then echo token=present >> '{{Log}}'; else echo token=absent >> '{{Log}}'; fi
        exit 0
        """);

    [Fact(SkipUnless = nameof(IsLinux), Skip = "execve: Linux only")]
    [SupportedOSPlatform("linux")]
    public async Task McpServer_ExecsServerWithExactArgvEnvNamesAndNoToken()
    {
        // Arrange
        InstallLoggingServer();

        // Act
        var run = await McpServer();

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        var log = File.ReadAllLines(Log);
        log[0].Should().Be("argv=--org-mode --http 127.0.0.1:47365 --http-local-file-tools --no-dynamic-registration");
        log[1].Should().Be("names=ENABLED_TOOLS HOME LC_ALL MS365_MCP_CLIENT_ID MS365_MCP_ORG_MODE MS365_MCP_TENANT_ID MS365_MCP_TOKEN_CACHE_PATH MS365_MCP_USE_KEYTAR NODE_OPTIONS PATH ");
        log[2].Should().Be("token=absent");
        Directory.Exists(Path.Combine(_fixture.Home, ".cache", "zyggy-m365-downloads")).Should().BeTrue();
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "execve and signals: Linux only")]
    [SupportedOSPlatform("linux")]
    public async Task McpServer_SigtermReachesServer_ExitCodeIsServers()
    {
        // Arrange: the fake traps TERM and exits 42
        var ready = Path.Combine(_fixture.Root, "ready");
        M365InstanceFixture.WriteScript(Server, $$"""
            #!/bin/sh
            trap 'exit 42' TERM
            echo ready > '{{ready}}'
            while :; do sleep 0.1; done
            """);
        using var process = ZyggyCli.Start(["m365", "mcp-server"], _fixture.Env(), _fixture.Root);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!File.Exists(ready) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        // Act
        var cmdline = await File.ReadAllTextAsync($"/proc/{process.Id}/cmdline", TestContext.Current.CancellationToken);
        using (var kill = Process.Start("/bin/kill", ["-TERM", process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)]))
        {
            await kill.WaitForExitAsync(TestContext.Current.CancellationToken);
        }

        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        // Assert: the zyggy process became the server (same pid), and the server's code came back
        File.Exists(ready).Should().BeTrue();
        cmdline.Should().Contain("ms-365-mcp-server");
        process.ExitCode.Should().Be(42);
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "Linux only")]
    [SupportedOSPlatform("linux")]
    public async Task McpServer_ServerOutsideHomeLocal_ExitThree()
    {
        // Arrange
        var elsewhere = Path.Combine(_fixture.Root, "elsewhere");
        M365InstanceFixture.WriteScript(Path.Combine(elsewhere, "ms-365-mcp-server"), "#!/bin/sh\nexit 0\n");
        var env = _fixture.Env();
        env["PATH"] = elsewhere + ":/usr/bin:/bin";

        // Act
        var run = await McpServer(env);

        // Assert
        run.ExitCode.Should().Be(3);
        run.Stderr.Should().Be($"m365: ms-365-mcp-server at {Path.Combine(elsewhere, "ms-365-mcp-server")} is not under {_fixture.Home}/.local — runbook 13 \"Install or upgrade the MCP server\"\n");
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "Linux only")]
    [SupportedOSPlatform("linux")]
    public async Task McpServer_ConfigInvalid_ExitThreeServerNotStarted()
    {
        // Arrange
        InstallLoggingServer();
        File.WriteAllText(Path.Combine(_fixture.InstanceDirectory, "m365.json"), "{");

        // Act
        var run = await McpServer();

        // Assert
        run.ExitCode.Should().Be(3);
        run.Stderr.Should().StartWith("m365: configuration error: ");
        File.Exists(Log).Should().BeFalse();
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "Linux only")]
    [SupportedOSPlatform("linux")]
    public async Task McpServer_HooksOff_Accepted()
    {
        // Arrange
        InstallLoggingServer();
        var env = _fixture.Env();
        env["ZYGGY_HOOKS"] = "off";

        // Act
        var run = await McpServer(env);

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        File.Exists(Log).Should().BeTrue();
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("--probe", "extra")]
    public async Task McpServer_BadArgument_ExitFour(params string[] args)
    {
        // Act
        var run = await McpServer(null, args);

        // Assert
        run.ExitCode.Should().Be(4);
        run.Stderr.Should().Be($"m365: unexpected argument '{args[0]}' (usage: zyggy m365 mcp-server [--probe])\n");
    }

    [Fact(SkipUnless = nameof(IsNotLinux), Skip = "the refusal off Linux")]
    public async Task McpServer_OnWindows_ExitThreeNotSupported()
    {
        // Act
        var run = await McpServer();

        // Assert
        run.ExitCode.Should().Be(3);
        run.Stderr.Should().Be("m365: not supported on this platform\n");
    }
}
