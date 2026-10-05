using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.M365;

/// <summary>Step 12 (spec 33 AC-23): <c>zyggy m365 verify</c> in process over the stubbed Graph, and its usage and configuration from the binary.</summary>
public sealed class VerifyCommandTests : IDisposable
{
    private const string Window = "2026-09-30T04:00:00Z";
    private const string Usage = " (usage: zyggy m365 verify <date YYYY-MM-DD> <window-start YYYY-MM-DDTHH:MM:SSZ>)\n";

    private readonly M365InstanceFixture _fixture = new();
    private readonly StubGraphHandler _graph = StubGraphHandler.FromGoldenRoutes();
    private readonly TestCertificates _pair;

    public VerifyCommandTests()
    {
        _pair = TestCertificates.Create(Path.Combine(_fixture.Home, ".config", "zyggy"));
        Directory.CreateDirectory(State);
        File.WriteAllText(Path.Combine(State, "replied-2026-09-30.ids"), "m1\n");
    }

    public static bool IsLinux => OperatingSystem.IsLinux();

    private string State => Path.Combine(_fixture.StateDirectory, "m365");

    private string Receipt => Path.Combine(State, "brief-2026-09-30.json");

    public void Dispose()
    {
        _graph.Violations.Should().BeEmpty();
        _pair.Dispose();
        _fixture.Dispose();
    }

    [Fact]
    public async Task Verify_Ok_ExitZeroAuditOkReceipt()
    {
        // Act
        var (exit, console) = await M365InProcess.RunAsync(_fixture.Env(), _graph, ["verify", "2026-09-30", Window]);

        // Assert
        exit.Should().Be(0, console.Stderr);
        console.Stdout.Should().Be("audit ok\n");
        console.Stderr.Should().BeEmpty();
        File.ReadAllBytes(Receipt).Should().Equal(File.ReadAllBytes(M365InstanceFixture.Golden("m365", "expected", "m365-receipt-ok.json")));
        if (OperatingSystem.IsLinux())
        {
            File.GetUnixFileMode(Receipt).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public async Task Verify_Flagged_ExitFive()
    {
        // Arrange
        _graph.Once("GET", "mailFolders/drafts/messages\\?\\$filter", 200, File.ReadAllText(StubGraphHandler.GraphFixture("drafts-flagged.json")));

        // Act
        var (exit, console) = await M365InProcess.RunAsync(_fixture.Env(), _graph, ["verify", "2026-09-30", Window]);

        // Assert
        exit.Should().Be(5);
        console.Stdout.Should().StartWith("audit FLAGGED: brief draft has recipients other than the owner (mallory@external.example); ");
    }

    [Fact]
    public async Task Verify_Graph403_ExitSixNoReceipt()
    {
        // Arrange
        _graph.Once("GET", "mailFolders/drafts/messages\\?\\$filter", 403, File.ReadAllText(StubGraphHandler.GraphFixture("graph-forbidden.json")));

        // Act
        var (exit, console) = await M365InProcess.RunAsync(_fixture.Env(), _graph, ["verify", "2026-09-30", Window]);

        // Assert
        exit.Should().Be(6);
        console.Stdout.Should().BeEmpty();
        console.Stderr.Should().Be("m365-verify: forbidden (ErrorAccessDenied) — runbook 13 \"Scope or grant missing\"\n");
        File.Exists(Receipt).Should().BeFalse();
    }

    [Theory]
    [InlineData(new string[0], "needs exactly <date> <window-start>")]
    [InlineData(new[] { "2026-09-30" }, "needs exactly <date> <window-start>")]
    [InlineData(new[] { "2026-9-30", Window }, "'2026-9-30' is not a date")]
    [InlineData(new[] { "2026-09-30", "yesterday" }, "'yesterday' is not an ISO timestamp")]
    [InlineData(new[] { "2026-09-30", "2026-09-30T04:00:00" }, "'2026-09-30T04:00:00' is not an ISO timestamp")]
    [InlineData(new[] { "2026-09-30", Window, "extra" }, "needs exactly <date> <window-start>")]
    public async Task Verify_BadArguments_ExitFour(string[] args, string message)
    {
        // Act
        var run = await ZyggyCli.RunAsync(["m365", "verify", .. args], _fixture.Env(), null, _fixture.Root, TestContext.Current.CancellationToken);

        // Assert
        run.ExitCode.Should().Be(4);
        run.Stdout.Should().BeEmpty();
        run.Stderr.Should().Be("m365-verify: " + message + Usage);
    }

    [Fact]
    public async Task Verify_ConfigInvalid_ExitThree()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_fixture.InstanceDirectory, "m365.json"), "{");

        // Act
        var run = await ZyggyCli.RunAsync(["m365", "verify", "2026-09-30", Window], _fixture.Env(), null, _fixture.Root, TestContext.Current.CancellationToken);

        // Assert
        run.ExitCode.Should().Be(3);
        run.Stderr.Should().StartWith("m365-verify: configuration error: ").And.EndWith("is not valid JSON\n");
        File.Exists(Receipt).Should().BeFalse();
    }

    [Fact]
    public async Task Verify_TenantUnset_ExitThree()
    {
        // Arrange
        var env = _fixture.Env();
        env["ZYGGY_TENANT"] = null;

        // Act
        var run = await ZyggyCli.RunAsync(["m365", "verify", "2026-09-30", Window], env, null, _fixture.Root, TestContext.Current.CancellationToken);

        // Assert
        run.ExitCode.Should().Be(3);
        run.Stderr.Should().Be("m365-verify: configuration error: ZYGGY_TENANT is not set\n");
    }
}
