using System.Runtime.Versioning;
using System.Text.Json;

using Zyggy.Core.LinkedIn;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.LinkedIn;

/// <summary>
/// Publishing in process over a real socket and real files (spec 36 AC-2, AC-5, AC-12, AC-13, AC-22): the token <c>auth finish</c> wrote is
/// read from its file, one post reaches the stand-in with the exact body, the row and the fact land on disk with their modes, failures
/// leave one row after exactly one request, a repeat is refused, and the token appears nowhere but its file.
/// </summary>
public sealed class PublishEndToEndTests : IAsyncLifetime
{
    private const string GoldenText = "Agents (part 1) [draft] {v2} <b> a|b @name *bold* _it_ ~x~ back\\slash #dotnet # not\nSecond line 🚀\nThird";
    private const string Urn = StubLinkedInServer.PostUrn;

    private readonly LinkedInFixture _fixture = new();
    private readonly StubLinkedInServer _stub = new();

    public static bool IsLinux => OperatingSystem.IsLinux();

    public static bool IsWindows => OperatingSystem.IsWindows();

    private string ActionLog => Path.Combine(_fixture.StateDirectory, "actions.jsonl");

    private string FactFile => Path.Combine(_fixture.PrincipalDirectory, "inbox", $"linkedin-{DateTime.UtcNow:yyyy-MM-dd}.md");

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await _stub.DisposeAsync();
        _fixture.Dispose();
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "the credential files: Linux only")]
    [SupportedOSPlatform("linux")]
    public async Task Publish_OnLinux_OnePostExactBodyHeaders_ResultPublished_Row0600_FactFileInInbox()
    {
        // Arrange
        await LinkedInInProcess.ConnectAsync(_fixture, _stub);
        using var tool = new LinkedInInProcess(_fixture, _stub);

        // Act
        var result = await tool.PublishAsync(GoldenText);

        // Assert
        result.Should().Be(new ToolCallResult(false, $"published: {Urn} — https://www.linkedin.com/feed/update/{Urn}/"));
        var post = _stub.To(StubLinkedInServer.PostsPath).Should().ContainSingle().Subject;
        post.Method.Should().Be("POST");
        post.Body.Should().Be(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "golden", "linkedin", "post-request.json")));
        post.Headers["Authorization"].Should().Be("Bearer " + LinkedInFixture.AccessToken);
        post.Headers["Linkedin-Version"].Should().Be("202609");
        post.Headers["X-Restli-Protocol-Version"].Should().Be("2.0.0");
        post.Headers["Content-Type"].Should().Be("application/json");
        var row = JsonDocument.Parse(File.ReadAllLines(ActionLog).Single()).RootElement;
        row.GetProperty("status").GetString().Should().Be("ok");
        row.GetProperty("urn").GetString().Should().Be(Urn);
        row.GetProperty("text").GetString().Should().Be(GoldenText);
        File.GetUnixFileMode(ActionLog).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.ReadAllText(FactFile).Should().Contain($"(linkedin {Urn}): Posted on LinkedIn (PUBLIC): \"Agents (part 1) [draft] {{v2}} <b> a|b @name *bold* _it_ ~x~ back\\slash #dotnet # not\"");
        tool.Diagnostics.Should().BeEmpty();
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "the credential files: Linux only")]
    [SupportedOSPlatform("linux")]
    public async Task Publish_WithImage_OnLinux_InitializeUploadPost_BytesExact_Row_Fact()
    {
        // Arrange: no settle wait in the test; the image in the default media folder under HOME
        await LinkedInInProcess.ConnectAsync(_fixture, _stub);
        var instance = Path.Combine(_fixture.InstanceDirectory, "linkedin.json");
        File.WriteAllText(instance, File.ReadAllText(instance).TrimEnd().TrimEnd('}') + ",\"image\":{\"settle_ms\":0}}\n");
        var media = Path.Combine(_fixture.Home, ".local", "share", "zyggy", "linkedin", "media");
        Directory.CreateDirectory(media);
        var png = new byte[200];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 0, 0, 4, 0xB0, 0, 0, 4, 0xB0, 8, 2 }.CopyTo(png, 0);
        var image = Path.Combine(media, "deny.png");
        File.WriteAllBytes(image, png);
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(png));
        using var tool = new LinkedInInProcess(_fixture, _stub);

        // Act
        var result = await tool.PublishWithImageAsync("Hello", image, sha, "A thumb on Deny");

        // Assert
        result.Should().Be(new ToolCallResult(false, $"published: {Urn} — https://www.linkedin.com/feed/update/{Urn}/"));
        _stub.To(StubLinkedInServer.ImagesPath).Should().ContainSingle().Which.Body.Should().Be("""{"initializeUploadRequest":{"owner":"urn:li:person:sub-alice-0001"}}""");
        var upload = _stub.Requests.Where(r => r.Method == "PUT").Should().ContainSingle().Subject;
        upload.Bytes.Should().Equal(png);
        upload.Headers["Authorization"].Should().Be("Bearer " + LinkedInFixture.AccessToken);
        _stub.To(StubLinkedInServer.PostsPath).Single().Body
            .Should().Be(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "golden", "linkedin", "post-request-image.json")));
        var row = JsonDocument.Parse(File.ReadAllLines(ActionLog).Single()).RootElement;
        row.GetProperty("image_sha256").GetString().Should().Be(sha);
        row.GetProperty("image_urn").GetString().Should().Be(StubLinkedInServer.ImageUrn);
        File.ReadAllText(FactFile).Should().Contain($"(linkedin {Urn}): Posted on LinkedIn (PUBLIC): \"Hello\" (with an image)");
        _fixture.EverythingOutsideCredentials().Should().NotContain(LinkedInFixture.AccessToken);
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "the credential files: Linux only")]
    [SupportedOSPlatform("linux")]
    public async Task Publish_OnLinux_Stub500_OutcomeUnknownRow_ExactlyOneRequest_NoFact()
    {
        // Arrange
        await LinkedInInProcess.ConnectAsync(_fixture, _stub);
        using var tool = new LinkedInInProcess(_fixture, _stub);
        _stub.Once(StubLinkedInServer.PostsPath, 500);

        // Act
        var result = await tool.PublishAsync("A post that may or may not exist.");

        // Assert
        result.Should().Be(new ToolCallResult(true, "outcome_unknown: the post may exist — check your profile before asking again"));
        _stub.To(StubLinkedInServer.PostsPath).Should().ContainSingle();
        File.ReadAllLines(ActionLog).Should().ContainSingle().Which.Should().Contain("\"status\":\"outcome_unknown: the post may exist");
        File.Exists(FactFile).Should().BeFalse();
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "the credential files: Linux only")]
    [SupportedOSPlatform("linux")]
    public async Task Publish_OnLinux_StubSlowerThanTimeout_OutcomeUnknown_OneRequest()
    {
        // Arrange: the in-process timeout is 1 s
        await LinkedInInProcess.ConnectAsync(_fixture, _stub);
        using var tool = new LinkedInInProcess(_fixture, _stub);
        _stub.Once(StubLinkedInServer.PostsPath, 201, string.Empty, $"x-restli-id: {Urn}\r\n", delay: TimeSpan.FromSeconds(3));

        // Act
        var result = await tool.PublishAsync("A slow post.");

        // Assert
        result.Text.Should().StartWith("outcome_unknown: ");
        _stub.To(StubLinkedInServer.PostsPath).Should().ContainSingle();
        File.Exists(FactFile).Should().BeFalse();
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "the credential files: Linux only")]
    [SupportedOSPlatform("linux")]
    public async Task Publish_OnLinux_SameTextTwice_SecondRefusedDuplicate_StubSawOne()
    {
        // Arrange
        await LinkedInInProcess.ConnectAsync(_fixture, _stub);
        using var tool = new LinkedInInProcess(_fixture, _stub);
        (await tool.PublishAsync("Once only — déjà vu, ✨ and 🚀.")).IsError.Should().BeFalse();

        // Act
        var second = await tool.PublishAsync("Once only — déjà vu, ✨ and 🚀.", "CONNECTIONS");

        // Assert: the stand-in received the raw UTF-8 body whole (non-ASCII as bytes, outside the BMP as an escape)
        JsonDocument.Parse(_stub.To(StubLinkedInServer.PostsPath).Single().Body!).RootElement.GetProperty("commentary").GetString()
            .Should().Be("Once only — déjà vu, ✨ and 🚀.");
        second.IsError.Should().BeTrue();
        second.Text.Should().StartWith($"refused: duplicate of {Urn} posted ");
        _stub.To(StubLinkedInServer.PostsPath).Should().ContainSingle();
        File.ReadAllLines(ActionLog).Should().HaveCount(2);
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "the credential files: Linux only")]
    [SupportedOSPlatform("linux")]
    public async Task Publish_OnLinux_Stub401_TokenExpired_NoFact()
    {
        // Arrange
        await LinkedInInProcess.ConnectAsync(_fixture, _stub);
        using var tool = new LinkedInInProcess(_fixture, _stub);
        _stub.Once(StubLinkedInServer.PostsPath, 401);

        // Act
        var result = await tool.PublishAsync("Hello there.");

        // Assert
        result.Should().Be(new ToolCallResult(true, "token_expired: reconnect LinkedIn — runbook \"LinkedIn token expired\""));
        File.Exists(FactFile).Should().BeFalse();
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "the credential files: Linux only")]
    [SupportedOSPlatform("linux")]
    public async Task Publish_OnLinux_SecretScan_TokenOnlyInTokenFile()
    {
        // Arrange
        await LinkedInInProcess.ConnectAsync(_fixture, _stub);
        using var tool = new LinkedInInProcess(_fixture, _stub);
        var results = new List<ToolCallResult> { await tool.PublishAsync("First post. Read it.") };
        _stub.Once(StubLinkedInServer.PostsPath, 400, "{\"message\":\"bad token " + LinkedInFixture.AccessToken + "\"}");
        results.Add(await tool.PublishAsync("Second post."));
        _stub.Once(StubLinkedInServer.PostsPath, 500);

        // Act
        results.Add(await tool.PublishAsync("Third post."));

        // Assert
        var everything = _fixture.EverythingOutsideCredentials()
            + string.Join('\n', results.Select(r => r.Text))
            + tool.Diagnostics
            + string.Join('\n', _stub.Requests.Select(r => r.Target + "\n" + r.Body));
        everything.Should().NotContain(LinkedInFixture.AccessToken);
        File.ReadAllText(_fixture.TokenFile).Should().Contain(LinkedInFixture.AccessToken);
    }

    [Fact(SkipUnless = nameof(IsWindows), Skip = "the platform refusal: Windows only")]
    public async Task Publish_OnWindows_ConfigurationErrorNotSupported_NoRequest()
    {
        // Arrange
        using var tool = new LinkedInInProcess(_fixture, _stub);

        // Act
        var result = await tool.PublishAsync("Hello.");

        // Assert
        result.Should().Be(new ToolCallResult(true, "configuration_error: linkedin: not supported on this platform"));
        _stub.Requests.Should().BeEmpty();
        File.ReadAllLines(ActionLog).Should().ContainSingle();
    }

    [Fact]
    public async Task Publish_SecretPatternsMissing_ConfigurationErrorEveryCall_NoRequest()
    {
        // Arrange
        File.Delete(_fixture.SecretPatterns);
        using var tool = new LinkedInInProcess(_fixture, _stub);

        // Act
        var first = await tool.PublishAsync("Hello.");
        var second = await tool.PublishAsync("Hello again.");

        // Assert
        first.Should().Be(new ToolCallResult(true, $"configuration_error: {_fixture.SecretPatterns} is missing"));
        second.Should().Be(first);
        _stub.Requests.Should().BeEmpty();
        File.ReadAllLines(ActionLog).Should().HaveCount(2);
    }
}
