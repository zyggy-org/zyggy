using System.Diagnostics;

using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.M365;

/// <summary>Step 14 (spec 33 AC-24): <c>zyggy m365 auth-header</c>, the headersHelper Claude Code runs per connection.</summary>
public sealed class AuthHeaderCommandTests : IDisposable
{
    private const string Failure = "m365: token refresh failed — runbook 13 \"Certificate rejected\"\n";

    private readonly M365InstanceFixture _fixture = new();
    private readonly StubGraphHandler _graph = StubGraphHandler.FromGoldenRoutes();

    public static bool IsLinux => OperatingSystem.IsLinux();

    public void Dispose()
    {
        _graph.Violations.Should().BeEmpty();
        _fixture.Dispose();
    }

    [Fact]
    public async Task AuthHeader_Success_ExactlyOneJsonLineStderrEmpty()
    {
        // Arrange
        using var pair = TestCertificates.Create(Path.Combine(_fixture.Home, ".config", "zyggy"));
        var token = System.Text.Json.JsonDocument.Parse(File.ReadAllText(StubGraphHandler.GraphFixture("token-ok.json"))).RootElement.GetProperty("access_token").GetString();

        // Act
        var (exit, console) = await M365InProcess.RunAsync(_fixture.Env(), _graph, ["auth-header"]);

        // Assert
        exit.Should().Be(0, console.Stderr);
        console.Stdout.Should().Be($$"""{"Authorization":"Bearer {{token}}"}""" + "\n");
        console.Stderr.Should().BeEmpty();
    }

    [Fact]
    public async Task AuthHeader_InvalidClientTwice_StdoutEmptyStderrExactExitSix()
    {
        // Arrange
        using var pair = TestCertificates.Create(Path.Combine(_fixture.Home, ".config", "zyggy"));
        var invalid = File.ReadAllText(StubGraphHandler.GraphFixture("token-invalid-client.json"));
        _graph.Once("POST", "/oauth2/v2\\.0/token$", 400, invalid).Once("POST", "/oauth2/v2\\.0/token$", 400, invalid);

        // Act
        var (exit, console) = await M365InProcess.RunAsync(_fixture.Env(), _graph, ["auth-header"]);

        // Assert
        exit.Should().Be(6);
        console.Stdout.Should().BeEmpty();
        console.Stderr.Should().Be(Failure);
        _graph.Requests.Count(r => r.Method == HttpMethod.Post).Should().Be(2);
    }

    [Fact]
    public async Task AuthHeader_EnvWithoutKey_UsesDefaultKeyPathNoRetry()
    {
        // Act: no key at ~/.config/zyggy/m365-app.key — a key failure is not retried and never reaches the network
        var run = await ZyggyCli.RunAsync(["m365", "auth-header"], _fixture.Env(), null, _fixture.Root, TestContext.Current.CancellationToken);

        // Assert
        run.ExitCode.Should().Be(3);
        run.Stdout.Should().BeEmpty();
        run.Stderr.Should().Be(Failure);
    }

    [Fact]
    public async Task AuthHeader_Argument_ExitFour()
    {
        // Act
        var run = await ZyggyCli.RunAsync(["m365", "auth-header", "--now"], _fixture.Env(), null, _fixture.Root, TestContext.Current.CancellationToken);

        // Assert
        run.ExitCode.Should().Be(4);
        run.Stderr.Should().Be("m365: takes no argument (usage: zyggy m365 auth-header)\n");
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "helper latency on the CI runner: Linux only")]
    public async Task AuthHeader_OnLinux_FinishesUnder8Seconds()
    {
        // Arrange
        var watch = Stopwatch.StartNew();

        // Act
        var run = await ZyggyCli.RunAsync(["m365", "auth-header"], _fixture.Env(), null, _fixture.Root, TestContext.Current.CancellationToken);

        // Assert
        run.ExitCode.Should().Be(3);
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(8));
    }
}
