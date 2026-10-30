using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;

using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Hub;

/// <summary>
/// <c>zyggy hub</c> from the built binary over real stdio (spec 11 AC-1, AC-2, AC-9..AC-20, D1), driven by the hand-written JSON-RPC
/// client: both tools and their goldens, the ranked memory text, a kept fact shared with the CLI verb, the refusals, the exit codes,
/// stdout carrying only protocol lines, the settings fallback and no git.
/// </summary>
public sealed class HubMcpServerTests : IDisposable
{
    private const string Secret = "AKIAABCDEFGHIJKLMNOP";

    private readonly HubFixture _fixture = new();

    public static bool IsLinux => OperatingSystem.IsLinux();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Initialize_ServerInfoHub_VersionAndToolsCapability()
    {
        // Arrange
        await using var client = Start();

        // Act
        var initialize = await client.InitializeAsync();

        // Assert
        var result = initialize.GetProperty("result");
        result.GetProperty("serverInfo").GetProperty("name").GetString().Should().Be("hub");
        result.GetProperty("serverInfo").GetProperty("version").GetString().Should().NotBeNullOrEmpty();
        result.GetProperty("capabilities").TryGetProperty("tools", out _).Should().BeTrue();
        (await client.CloseAndWaitAsync()).Exit.Should().Be(0);
    }

    [Fact]
    public async Task ToolsList_ExactlyTwoTools_SchemasEqualGoldens()
    {
        // Arrange
        await using var client = Start();
        await client.InitializeAsync();

        // Act
        var list = await client.RequestAsync("tools/list");

        // Assert
        var tools = list.GetProperty("result").GetProperty("tools").EnumerateArray()
            .OrderBy(tool => tool.GetProperty("name").GetString(), StringComparer.Ordinal)
            .ToList();
        tools.Select(tool => tool.GetProperty("name").GetString()).Should().Equal("get_context", "remember");
        foreach (var tool in tools)
        {
            var name = tool.GetProperty("name").GetString();
            var golden = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "golden", "hub", $"tool-{name}.json")))!;
            tool.GetProperty("description").GetString().Should().Be(golden["description"]!.GetValue<string>());
            JsonNode.DeepEquals(JsonNode.Parse(tool.GetProperty("inputSchema").GetRawText()), golden["inputSchema"]).Should().BeTrue(name);
        }
    }

    [Fact]
    public async Task GetContext_Topic_RankedFilesAndDailyLines_TreeUnchanged()
    {
        // Arrange
        var before = _fixture.Fingerprint();
        await using var client = Start();
        await client.InitializeAsync();

        // Act
        var (text, isError) = await CallAsync(client, "get_context", """{"topic":"zyggy"}""");

        // Assert
        isError.Should().BeFalse();
        text.Should().StartWith("<zyggy-context tenant=\"acme\" user=\"alice\" generated=\"");
        text.Should().Contain(
            "## business/areas/zyggy.md — The platform Alice builds\n- [stated] 2026-10-20: Goal: an assistant on central.\n"
            + "## private/people/carol.md — Carol, who fixes the zyggy roof\n- [stated] 2026-10-25: Carol is my sister.\n"
            + "## inbox (picked up, not yet filed by the dream)\n- [stated] 2026-10-01: zyggy in the inbox\n"
            + "## daily\n- [observed] 09:15 session 1a2b3c4d: zyggy hub planned\n</zyggy-context>\n");
        // since spec 13 (Q2) the not-yet-filed remember- inbox line is returned in its own block; the rest stays excluded
        text.Should().NotContain("too old").And.NotContain("roll-up").And.NotContain("auto").And.NotContain("profile");
        _fixture.Fingerprint().Should().Equal(before);
    }

    [Fact]
    public async Task Remember_Fact_AppendsInboxLine_SharedWithCliVerb()
    {
        // Arrange
        await using var client = Start();
        await client.InitializeAsync();

        // Act — the Hub keeps one fact, then the CLI verb keeps another on the same tree.
        var (text, isError) = await CallAsync(client, "remember", """{"fact":"Marie prefers tea"}""");
        var verb = await ZyggyCli.RunAsync(["memory", "remember", "--", "Marie drinks it black"], _fixture.Env(), null, _fixture.Root, TestContext.Current.CancellationToken);

        // Assert
        isError.Should().BeFalse();
        var date = text.Split('\n')[1]["- [stated] ".Length..][..10];
        var file = Path.Combine(_fixture.InboxDirectory, $"remember-{date}.md");
        text.Should().Be($"remembered: {file}\n- [stated] {date}: Marie prefers tea\n");
        verb.ExitCode.Should().Be(0, verb.Stderr);
        verb.Stdout.Should().Be($"remembered: {file}\n- [stated] {date}: Marie drinks it black\n");
        File.ReadAllText(file).Should().Be(
            $"---\nname: remember {date}\ndescription: facts stated by the owner on {date} (remember skill)\nupdated: {date}\n---\n"
            + $"- [stated] {date}: Marie prefers tea\n- [stated] {date}: Marie drinks it black\n");
    }

    [Fact]
    public async Task Remember_SecretShaped_IsErrorNamesPattern_FactNowhere()
    {
        // Arrange
        var inboxBefore = Directory.EnumerateFiles(_fixture.InboxDirectory).ToList();
        await using var client = Start();
        await client.InitializeAsync();

        // Act
        var (text, isError) = await CallAsync(client, "remember", $$"""{"fact":"{{Secret}}"}""");
        var (exit, stderr) = await client.CloseAndWaitAsync();

        // Assert
        isError.Should().BeTrue();
        text.Should().Be("refused: matches secret pattern aws-access-key");
        exit.Should().Be(0);
        client.StdoutLines.Should().NotContain(line => line.Contains(Secret, StringComparison.Ordinal));
        stderr.Should().NotContain(Secret);
        Directory.EnumerateFiles(_fixture.Root, "*", SearchOption.AllDirectories)
            .Where(file => File.ReadAllText(file).Contains(Secret, StringComparison.Ordinal))
            .Should().BeEmpty();
        Directory.EnumerateFiles(_fixture.InboxDirectory).Should().Equal(inboxBefore);
    }

    [Fact]
    public async Task ToolsCall_TenantArgument_IsErrorRefused_OneStderrLine_NothingWritten()
    {
        // Arrange
        var before = _fixture.Fingerprint();
        await using var client = Start();
        await client.InitializeAsync();

        // Act
        var (text, isError) = await CallAsync(client, "remember", """{"fact":"a fact","tenant":"globex"}""");
        var (exit, stderr) = await client.CloseAndWaitAsync();

        // Assert
        isError.Should().BeTrue();
        text.Should().Be("refused: tenant and user are never request fields");
        exit.Should().Be(0);
        stderr.Should().Be("hub: refused: request named a principal\n");
        _fixture.Fingerprint().Should().Equal(before);
    }

    [Fact]
    public async Task ToolsCall_UnknownArgument_IsErrorInvalidArguments()
    {
        // Arrange
        await using var client = Start();
        await client.InitializeAsync();

        // Act
        var (text, isError) = await CallAsync(client, "get_context", """{"topic":"zyggy","extra":1}""");

        // Assert
        isError.Should().BeTrue();
        text.Should().Be("refused: invalid arguments");
    }

    [Fact]
    public async Task StdinClosed_ExitZero_NoStderr()
    {
        // Arrange
        await using var client = Start();
        await client.InitializeAsync();

        // Act
        var (exit, stderr) = await client.CloseAndWaitAsync();

        // Assert
        exit.Should().Be(0);
        stderr.Should().BeEmpty();
    }

    [Fact]
    public async Task Stdout_OnlyJsonRpcLines()
    {
        // Arrange
        await using var client = Start();
        await client.InitializeAsync();

        // Act
        await client.RequestAsync("tools/list");
        await CallAsync(client, "get_context", """{"topic":"zyggy"}""");
        await CallAsync(client, "remember", """{"fact":"a fact"}""");
        await CallAsync(client, "remember", $$"""{"fact":"{{Secret}}"}""");
        await client.CloseAndWaitAsync();

        // Assert
        client.StdoutLines.Should().NotBeEmpty().And.OnlyContain(line => IsJsonRpc(line));
    }

    [Fact]
    public async Task PrincipalUnset_ExitThreeOneStderrLine_NothingOnStdout()
    {
        // Arrange
        var env = _fixture.Env();
        env["ZYGGY_TENANT"] = null;

        // Act
        var run = await RunHubAsync(env);

        // Assert
        run.ExitCode.Should().Be(3);
        run.Stdout.Should().BeEmpty();
        run.Stderr.Should().Be("hub: configuration error: ZYGGY_TENANT is not set\n");
    }

    [Fact]
    public async Task PrincipalDirectoryMissing_ExitThree()
    {
        // Arrange
        var env = _fixture.Env();
        env["ZYGGY_USER"] = "bob";

        // Act
        var run = await RunHubAsync(env);

        // Assert
        run.ExitCode.Should().Be(3);
        run.Stdout.Should().BeEmpty();
        run.Stderr.Should().StartWith("hub: configuration error: memory directory ").And.EndWith(" does not exist (ZYGGY_TENANT/ZYGGY_USER)\n");
    }

    [Fact]
    public async Task PatternsMissing_ExitThree()
    {
        // Arrange
        var missing = Path.Combine(_fixture.Root, "missing.txt");
        var env = _fixture.Env();
        env["ZYGGY_SECRET_PATTERNS"] = missing;

        // Act
        var run = await RunHubAsync(env);

        // Assert
        run.ExitCode.Should().Be(3);
        run.Stdout.Should().BeEmpty();
        run.Stderr.Should().Be($"hub: configuration error: {missing} is missing\n");
    }

    [Fact]
    public async Task Proxy_ExitFourNamesDeliverable21()
    {
        // Act
        var run = await RunHubAsync(_fixture.Env(), "--proxy");

        // Assert
        run.ExitCode.Should().Be(4);
        run.Stdout.Should().BeEmpty();
        run.Stderr.Should().Be("hub: --proxy is deliverable 21; zyggy hub takes no arguments\n");
    }

    [Fact]
    public async Task ExtraArgument_ExitFour()
    {
        // Act
        var run = await RunHubAsync(_fixture.Env(), "get_context");

        // Assert
        run.ExitCode.Should().Be(4);
        run.Stderr.Should().Be("hub: takes no arguments\n");
    }

    [Fact]
    public async Task PrincipalFromSettingsLocalJson_UnderClaudeProjectDir_Served()
    {
        // Arrange — no ZYGGY_* at all; only the checkout Claude Code would name.
        var env = new Dictionary<string, string?>(StringComparer.Ordinal) { ["CLAUDE_PROJECT_DIR"] = _fixture.CheckoutWithSettings() };
        await using var client = McpStdioClient.Start(env, _fixture.Root, ["hub"]);

        // Act
        var initialize = await client.InitializeAsync();
        var (text, isError) = await CallAsync(client, "get_context", """{"topic":"zyggy"}""");

        // Assert
        initialize.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString().Should().Be("hub");
        isError.Should().BeFalse();
        text.Should().StartWith("<zyggy-context tenant=\"acme\" user=\"alice\" generated=\"");
        (await client.CloseAndWaitAsync()).Exit.Should().Be(0);
    }

    [Fact]
    public async Task HooksOff_RememberNotStored_GetContextServes()
    {
        // Arrange
        var before = _fixture.Fingerprint();
        var env = _fixture.Env();
        env["ZYGGY_HOOKS"] = "off";
        await using var client = McpStdioClient.Start(env, _fixture.Root, ["hub"]);
        await client.InitializeAsync();

        // Act
        var remember = await CallAsync(client, "remember", """{"fact":"Marie prefers tea"}""");
        var context = await CallAsync(client, "get_context", """{"topic":"zyggy"}""");

        // Assert
        remember.Should().Be(("not stored: hooks are off (unattended run)", false));
        context.IsError.Should().BeFalse();
        context.Text.Should().Contain("## business/areas/zyggy.md");
        _fixture.Fingerprint().Should().Equal(before);
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "PATH shim: Linux only")]
    [SupportedOSPlatform("linux")]
    public async Task Hub_NeverInvokesGit()
    {
        // Arrange
        var bin = Path.Combine(_fixture.Root, "bin");
        var marker = Path.Combine(_fixture.Root, "git-was-called");
        Directory.CreateDirectory(bin);
        var shim = Path.Combine(bin, "git");
        File.WriteAllText(shim, $"#!/bin/sh\ntouch '{marker}'\nexit 99\n");
        File.SetUnixFileMode(shim, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var env = _fixture.Env();
        env["PATH"] = bin + ":" + Environment.GetEnvironmentVariable("PATH");
        await using var client = McpStdioClient.Start(env, _fixture.Root, ["hub"]);
        await client.InitializeAsync();

        // Act
        await CallAsync(client, "remember", """{"fact":"a fact"}""");
        await CallAsync(client, "get_context", """{"topic":"zyggy"}""");
        var (exit, _) = await client.CloseAndWaitAsync();

        // Assert
        exit.Should().Be(0);
        File.Exists(marker).Should().BeFalse();
    }

    private static async Task<(string Text, bool IsError)> CallAsync(McpStdioClient client, string tool, string argumentsJson)
    {
        var response = await client.RequestAsync("tools/call", $$"""{"name":"{{tool}}","arguments":{{argumentsJson}}}""");
        var result = response.GetProperty("result");
        var content = result.GetProperty("content").EnumerateArray().Should().ContainSingle().Subject;
        content.GetProperty("type").GetString().Should().Be("text");
        var isError = result.TryGetProperty("isError", out var flag) && flag.ValueKind == JsonValueKind.True;
        return (content.GetProperty("text").GetString()!, isError);
    }

    private static bool IsJsonRpc(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.TryGetProperty("jsonrpc", out var version) && version.GetString() == "2.0";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private McpStdioClient Start() => McpStdioClient.Start(_fixture.Env(), _fixture.Root, ["hub"]);

    private Task<ZyggyRun> RunHubAsync(Dictionary<string, string?> env, params string[] args) =>
        ZyggyCli.RunAsync(["hub", .. args], env, null, _fixture.Root, TestContext.Current.CancellationToken);
}
