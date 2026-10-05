using System.Diagnostics;
using System.Text;

using Zyggy.Core.M365.Guard;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.M365;

/// <summary>
/// Step 11 (spec 33 AC-20, AC-21, AC-7): <c>zyggy m365 guard</c> as the PreToolUse hook. The send matrix, non-action tools and every
/// fail-closed path run on the built binary (no Graph read needed); upload and move run in process over the stubbed Graph.
/// </summary>
public sealed class GuardCommandTests : IDisposable
{
    private readonly M365InstanceFixture _fixture = new();
    private readonly StubGraphHandler _graph = StubGraphHandler.FromGoldenRoutes();
    private readonly TestCertificates _pair;
    private readonly StringBuilder _outputs = new();

    public GuardCommandTests() => _pair = TestCertificates.Create(Path.Combine(_fixture.Home, ".config", "zyggy"));

    public static bool IsLinux => OperatingSystem.IsLinux();

    public static TheoryData<string, string?> SendRows() => new()
    {
        { "send-clean", null },
        { "send-save-true", null },
        { "send-lowercase-keys", null },
        { "send-4000", null },
        { "send-10-recipients", null },
        { "send-attachment", "attachments are not allowed" },
        { "send-bcc", "Bcc is not allowed" },
        { "send-html", "only plain-text bodies" },
        { "send-no-contenttype", "only plain-text bodies" },
        { "send-long", "body over 4000 characters" },
        { "send-11-recipients", "more than 10 recipients" },
        { "send-save-false", "saveToSentItems must stay true" },
        { "send-other-mailbox", "only the configured mailbox" },
        { "send-malformed-address", "malformed address" },
        { "send-no-recipient", "no recipient" },
        { "send-duplicate-keys", "the body names a field twice" },
        { "send-stray-arg", "unexpected argument bccRecipients" },
        { "send-from", "from, sender and replyTo are not allowed" },
        { "send-headers", "message field not allowed" },
    };

    public static TheoryData<string, string?> UploadAndMoveRows() => new()
    {
        { "upload-clean", null },
        { "upload-exists", "target exists (would overwrite)" },
        { "upload-parent-file", "parent is not a folder" },
        { "upload-parent-absent", "parent is not a folder" },
        { "upload-foreign-drive", "drive not allowed" },
        { "move-archive", null },
        { "move-folder-id", null },
        { "move-excluded-id", "destination not allowed" },
        { "move-unknown-id", "destination not allowed" },
        { "move-recoverable", "destination not allowed" },
        { "move-purges", "destination not allowed" },
    };

    public void Dispose()
    {
        _graph.Violations.Should().BeEmpty();
        _pair.Dispose();
        _fixture.Dispose();
    }

    private static string Fixture(string name) => File.ReadAllText(M365InstanceFixture.Golden("m365", "fixtures", $"hook-{name}.json"));

    private async Task<ZyggyRun> Guard(string stdin, Dictionary<string, string?>? env = null, params string[] args)
    {
        var run = await ZyggyCli.RunAsync(["m365", "guard", .. args], env ?? _fixture.Env(), stdin, _fixture.Root, TestContext.Current.CancellationToken);
        _outputs.Append(run.Stdout).Append(run.Stderr);
        return run;
    }

    [Theory]
    [MemberData(nameof(SendRows))]
    public async Task Guard_SendFixture_DecisionAsShell(string fixture, string? reason)
    {
        // Act
        var run = await Guard(Fixture(fixture));

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        run.Stdout.Should().Be(reason is null ? string.Empty : GuardOutput.Deny(reason));
        NeverLeaks();
    }

    [Fact]
    public async Task Guard_SendClean_HooksOffAndUnset_SameDecision()
    {
        // Arrange
        var off = _fixture.Env();
        off["ZYGGY_HOOKS"] = "off";

        // Act
        var unset = await Guard(Fixture("send-bcc"));
        var hooksOff = await Guard(Fixture("send-bcc"), off);

        // Assert
        hooksOff.Stdout.Should().Be(unset.Stdout).And.Be(GuardOutput.Deny("Bcc is not allowed"));
    }

    [Fact]
    public async Task Guard_NonActionTool_ExitZeroNoOutput()
    {
        // Arrange: no configuration at all is needed for a tool the guard does not own
        var env = _fixture.Env();
        env["ZYGGY_INSTANCE_DIR"] = Path.Combine(_fixture.Root, "nowhere");

        // Act
        var run = await Guard(Fixture("other-tool"), env);

        // Assert
        run.ExitCode.Should().Be(0);
        run.Stdout.Should().BeEmpty();
        run.Stderr.Should().BeEmpty();
    }

    [Theory]
    [InlineData("", "m365-guard: hook input is not a PreToolUse object with tool_name and tool_input\n")]
    [InlineData("not json", "m365-guard: hook input is not a PreToolUse object with tool_name and tool_input\n")]
    [InlineData("[]", "m365-guard: hook input is not a PreToolUse object with tool_name and tool_input\n")]
    [InlineData("""{"tool_name": "mcp__m365__send-shared-mailbox-mail"}""", "m365-guard: hook input is not a PreToolUse object with tool_name and tool_input\n")]
    public async Task Guard_NotHookJson_ExitTwoOneStderrLineNoStdout(string stdin, string stderr)
    {
        // Act
        var run = await Guard(stdin);

        // Assert
        run.ExitCode.Should().Be(2);
        run.Stdout.Should().BeEmpty();
        run.Stderr.Should().Be(stderr);
    }

    [Fact]
    public async Task Guard_ConfigMissing_ExitTwo()
    {
        // Arrange
        File.Delete(Path.Combine(_fixture.InstanceDirectory, "m365.json"));

        // Act
        var run = await Guard(Fixture("send-clean"));

        // Assert
        run.ExitCode.Should().Be(2);
        run.Stdout.Should().BeEmpty();
        run.Stderr.Should().StartWith("m365-guard: configuration error: ").And.EndWith("m365.json not found (ZYGGY_M365_CONFIG)\n");
    }

    [Fact]
    public async Task Guard_BadConfiguration_ExitTwoShellMessage()
    {
        // Arrange
        var config = Path.Combine(_fixture.InstanceDirectory, "m365.json");
        File.WriteAllText(config, File.ReadAllText(config).Replace("11111111-1111-4111-8111-111111111111", "x", StringComparison.Ordinal));

        // Act
        var run = await Guard(Fixture("send-clean"));

        // Assert
        run.ExitCode.Should().Be(2);
        run.Stderr.Should().Be("m365-guard: configuration error: tenant_id is not a GUID\n");
    }

    [Fact]
    public async Task Guard_TenantUnset_ExitTwo()
    {
        // Arrange
        var env = _fixture.Env();
        env["ZYGGY_TENANT"] = null;

        // Act
        var run = await Guard(Fixture("send-clean"), env);

        // Assert
        run.ExitCode.Should().Be(2);
        run.Stdout.Should().BeEmpty();
        run.Stderr.Should().Be("m365-guard: configuration error: ZYGGY_TENANT is not set\n");
    }

    [Fact]
    public async Task Guard_AnyArgument_ExitTwo()
    {
        // Act
        var run = await Guard(Fixture("send-clean"), null, "--allow");

        // Assert
        run.ExitCode.Should().Be(2);
        run.Stdout.Should().BeEmpty();
        run.Stderr.Should().Be("m365-guard: takes no arguments\n");
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "hook latency on the CI runner: Linux only")]
    public async Task Guard_OnLinux_NonActionTool_FinishesUnderTwoSeconds()
    {
        // Arrange
        var watch = Stopwatch.StartNew();

        // Act
        var run = await Guard(Fixture("other-tool"));

        // Assert
        run.ExitCode.Should().Be(0);
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }

    [Theory]
    [MemberData(nameof(UploadAndMoveRows))]
    public async Task Guard_UploadAndMoveFixtures_DecisionAsShell(string fixture, string? reason)
    {
        // Act
        var (exit, console) = await M365InProcess.RunAsync(_fixture.Env(), _graph, ["guard"], stdin: Fixture(fixture));

        // Assert
        exit.Should().Be(0, console.Stderr);
        console.Stdout.Should().Be(reason is null ? string.Empty : GuardOutput.Deny(reason));
        _outputs.Append(console.Stdout).Append(console.Stderr);
        NeverLeaks();
    }

    [Theory]
    [InlineData("upload-clean", "items/01PARENT0001\\?", 500, "m365-guard: graph read item-kind failed\n")]
    [InlineData("move-folder-id", "mailFolders\\?", 403, "m365-guard: graph read mail-folders failed\n")]
    public async Task Guard_GraphReadFails_ExitTwo(string fixture, string route, int status, string stderr)
    {
        // Arrange
        _graph.Once("GET", route, status, File.ReadAllText(StubGraphHandler.GraphFixture("graph-forbidden.json")));

        // Act
        var (exit, console) = await M365InProcess.RunAsync(_fixture.Env(), _graph, ["guard"], stdin: Fixture(fixture));

        // Assert
        exit.Should().Be(2);
        console.Stdout.Should().BeEmpty();
        console.Stderr.Should().Be(stderr);
    }

    // Never a token, a mail body or file content in any output the guard produced.
    private void NeverLeaks()
    {
        var all = _outputs.ToString();
        all.Should().NotContainAny("STUBACCESS", "BODYTEXT-NEVER-STORED", "UPLOADTEXT-NEVER-LOGGED", "Hello Carol", "VVBMT0FEVEVYVC1ORVZFUi1MT0dHRUQ", "\"allow\"", "\"ask\"");
    }
}
