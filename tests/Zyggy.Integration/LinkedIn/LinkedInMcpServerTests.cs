using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;

using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.LinkedIn;

/// <summary>
/// <c>zyggy linkedin mcp-server</c> from the built binary over real stdio (spec 36 AC-1, AC-2, AC-6), driven by a hand-written JSON-RPC
/// client: the one tool and its schema, a published post through the loopback stand-in, the refusals, the exit codes, stdout carrying
/// only protocol lines.
/// </summary>
public sealed class LinkedInMcpServerTests : IAsyncLifetime
{
    private readonly LinkedInFixture _fixture = new();
    private readonly StubLinkedInServer _stub = new();

    public static bool IsLinux => OperatingSystem.IsLinux();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await _stub.DisposeAsync();
        _fixture.Dispose();
    }

    [Fact]
    public async Task Initialize_ServerInfoLinkedin_ToolsCapability()
    {
        // Arrange
        await using var client = Start();

        // Act
        var initialize = await client.InitializeAsync();

        // Assert
        var result = initialize.GetProperty("result");
        result.GetProperty("serverInfo").GetProperty("name").GetString().Should().Be("linkedin");
        result.GetProperty("capabilities").TryGetProperty("tools", out _).Should().BeTrue();
        (await client.CloseAndWaitAsync()).Exit.Should().Be(0);
    }

    [Fact]
    public async Task ToolsList_ExactlyPublishPost_SchemaEqualsGolden()
    {
        // Arrange
        await using var client = Start();
        await client.InitializeAsync();

        // Act
        var list = await client.RequestAsync("tools/list");

        // Assert
        var tool = list.GetProperty("result").GetProperty("tools").EnumerateArray().Should().ContainSingle().Subject;
        var golden = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "golden", "linkedin", "tool-publish_post.json")))!;
        tool.GetProperty("name").GetString().Should().Be("publish_post");
        tool.GetProperty("description").GetString().Should().Be(golden["description"]!.GetValue<string>());
        JsonNode.DeepEquals(JsonNode.Parse(tool.GetProperty("inputSchema").GetRawText()), golden["inputSchema"]).Should().BeTrue(tool.GetProperty("inputSchema").GetRawText());
    }

    [Fact]
    public async Task ToolsList_ActionsEnabledEmpty_NoTools()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_fixture.InstanceDirectory, "linkedin.json"), """{"client_id":"clientid0001","redirect_uri":"https://localhost/zyggy/linkedin","actions":{"enabled":[]}}""");
        await using var client = Start();
        await client.InitializeAsync();

        // Act
        var list = await client.RequestAsync("tools/list");

        // Assert
        list.GetProperty("result").GetProperty("tools").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task ToolsCall_InvalidArguments_IsErrorRefused_NoRequest()
    {
        // Arrange
        await using var client = Start();
        await client.InitializeAsync();

        // Act
        var call = await client.RequestAsync("tools/call", """{"name":"publish_post","arguments":{"text":"Hello","visibility":"PUBLIC","extra":true}}""");

        // Assert
        var result = call.GetProperty("result");
        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        result.GetProperty("content")[0].GetProperty("text").GetString().Should().Be("refused: invalid arguments");
        _stub.Requests.Should().BeEmpty();
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "the credential files: Linux only")]
    [SupportedOSPlatform("linux")]
    public async Task ToolsCall_OnLinux_ValidText_PublishedResult_StubSawOnePost_Row0600()
    {
        // Arrange
        await LinkedInInProcess.ConnectAsync(_fixture, _stub);
        await using var client = Start();
        await client.InitializeAsync();

        // Act
        var call = await client.RequestAsync("tools/call", """{"name":"publish_post","arguments":{"text":"Hello from the server — once.","visibility":"CONNECTIONS"}}""");

        // Assert
        var result = call.GetProperty("result");
        (!result.TryGetProperty("isError", out var isError) || isError.ValueKind == JsonValueKind.False).Should().BeTrue();
        result.GetProperty("content")[0].GetProperty("text").GetString()
            .Should().Be($"published: {StubLinkedInServer.PostUrn} — https://www.linkedin.com/feed/update/{StubLinkedInServer.PostUrn}/");
        var post = _stub.To(StubLinkedInServer.PostsPath).Should().ContainSingle().Subject;
        JsonDocument.Parse(post.Body!).RootElement.GetProperty("visibility").GetString().Should().Be("CONNECTIONS");
        var log = Path.Combine(_fixture.StateDirectory, "actions.jsonl");
        File.ReadAllLines(log).Should().ContainSingle().Which.Should().Contain("\"status\":\"ok\"");
        File.GetUnixFileMode(log).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "the credential files: Linux only")]
    [SupportedOSPlatform("linux")]
    public async Task ToolsCall_NotConnected_IsErrorNotConnected()
    {
        // Arrange
        await using var client = Start();
        await client.InitializeAsync();

        // Act
        var call = await client.RequestAsync("tools/call", """{"name":"publish_post","arguments":{"text":"Hello","visibility":"PUBLIC"}}""");

        // Assert
        var result = call.GetProperty("result");
        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        result.GetProperty("content")[0].GetProperty("text").GetString().Should().Be("not_connected: say \"connect LinkedIn\"");
        _stub.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task StdinClosed_ExitZero()
    {
        // Arrange
        await using var client = Start();
        await client.InitializeAsync();

        // Act
        var (exit, stderr) = await client.CloseAndWaitAsync();

        // Assert
        exit.Should().Be(0, stderr);
    }

    [Fact]
    public async Task HooksOff_ExitFive_NoStdout()
    {
        // Arrange
        var env = _fixture.Env(_stub.Port);
        env["ZYGGY_HOOKS"] = "off";

        // Act
        var run = await ZyggyCli.RunAsync(["linkedin", "mcp-server"], env, null, _fixture.Root, TestContext.Current.CancellationToken);

        // Assert
        run.ExitCode.Should().Be(5);
        run.Stdout.Should().BeEmpty();
        run.Stderr.Should().Be("linkedin: refused in an unattended run\n");
    }

    [Fact]
    public async Task ConfigMissing_ExitThree_OneStderrLine()
    {
        // Arrange
        File.Delete(Path.Combine(_fixture.InstanceDirectory, "linkedin.json"));

        // Act
        var run = await ZyggyCli.RunAsync(["linkedin", "mcp-server"], _fixture.Env(_stub.Port), null, _fixture.Root, TestContext.Current.CancellationToken);

        // Assert
        run.ExitCode.Should().Be(3);
        run.Stdout.Should().BeEmpty();
        run.Stderr.Should().Be($"linkedin: configuration error: {Path.Combine(_fixture.InstanceDirectory, "linkedin.json")} is missing\n");
    }

    [Fact]
    public async Task Stdout_OnlyJsonRpcLines()
    {
        // Arrange
        await using var client = Start();
        await client.InitializeAsync();
        await client.RequestAsync("tools/list");
        await client.RequestAsync("tools/call", """{"name":"publish_post","arguments":{"text":"","visibility":"PUBLIC"}}""");

        // Act
        await client.CloseAndWaitAsync();

        // Assert
        client.StdoutLines.Should().HaveCountGreaterThanOrEqualTo(3).And.OnlyContain(line => JsonDocument.Parse(line, default).RootElement.GetProperty("jsonrpc").GetString() == "2.0");
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "the credential files: Linux only")]
    [SupportedOSPlatform("linux")]
    public async Task OnLinux_SecretScan_TokenNotInStdoutStderr()
    {
        // Arrange
        await LinkedInInProcess.ConnectAsync(_fixture, _stub);
        await using var client = Start();
        await client.InitializeAsync();
        await client.RequestAsync("tools/call", """{"name":"publish_post","arguments":{"text":"First.","visibility":"PUBLIC"}}""");
        _stub.Once(StubLinkedInServer.PostsPath, 401);
        await client.RequestAsync("tools/call", """{"name":"publish_post","arguments":{"text":"Second.","visibility":"PUBLIC"}}""");

        // Act
        var (_, stderr) = await client.CloseAndWaitAsync();

        // Assert
        (string.Join('\n', client.StdoutLines) + stderr).Should().NotContain(LinkedInFixture.AccessToken).And.NotContain(LinkedInFixture.ClientSecret);
    }

    [Fact]
    public async Task ExtraArgument_ExitFour()
    {
        // Act
        var run = await ZyggyCli.RunAsync(["linkedin", "mcp-server", "--port", "80"], _fixture.Env(_stub.Port), null, _fixture.Root, TestContext.Current.CancellationToken);

        // Assert
        run.ExitCode.Should().Be(4);
        run.Stdout.Should().BeEmpty();
    }

    private McpStdioClient Start() => McpStdioClient.Start(_fixture.Env(_stub.Port), _fixture.Root);
}
