using System.Runtime.Versioning;
using System.Text.Json;

using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.M365;

/// <summary>Step 11 (spec 33 AC-22): <c>zyggy m365 log</c> as the PostToolUse hook, from the built binary.</summary>
public sealed class LogCommandTests : IDisposable
{
    private static readonly string[] RowKeys = ["ts", "session_id", "tool", "summary", "status"];

    private readonly M365InstanceFixture _fixture = new();

    public static bool IsLinux => OperatingSystem.IsLinux();

    public void Dispose() => _fixture.Dispose();

    private string ActionsFile => Path.Combine(_fixture.StateDirectory, "m365", "actions.jsonl");

    private static string Fixture(string name) => File.ReadAllText(M365InstanceFixture.Golden("m365", "fixtures", $"hook-{name}.json"));

    private Task<ZyggyRun> Log(string stdin, Dictionary<string, string?>? env = null, params string[] args) =>
        ZyggyCli.RunAsync(["m365", "log", .. args], env ?? _fixture.Env(), stdin, _fixture.Root, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Log_SendUploadMove_OneRowEachShape()
    {
        // Act
        foreach (var fixture in new[] { "post-send-ok", "post-upload-ok", "post-move-ok", "post-send-error", "post-move-error-text" })
        {
            var run = await Log(Fixture(fixture));
            run.ExitCode.Should().Be(0, run.Stderr);
            run.Stdout.Should().BeEmpty();
            run.Stderr.Should().BeEmpty();
        }

        // Assert
        var rows = File.ReadAllLines(ActionsFile).Select(l => JsonDocument.Parse(l).RootElement).ToList();
        rows.Should().HaveCount(5);
        rows.Should().OnlyContain(r => r.EnumerateObject().Select(p => p.Name).SequenceEqual(RowKeys));
        rows.Select(r => r.GetProperty("status").GetString()).Should().Equal("ok", "ok", "ok", "error: ErrorAccessDenied", "error: ErrorItemNotFound");
        rows[2].GetProperty("summary").GetString().Should().Be("message m1 -> archive");
        File.ReadAllText(ActionsFile).Should().NotContainAny("BODYTEXT-NEVER-STORED", "Hello Carol", "UPLOADTEXT-NEVER-LOGGED", "STUBACCESS");
    }

    [Fact]
    public async Task Log_OtherTool_NoRow()
    {
        // Act
        var run = await Log(Fixture("other-tool"));

        // Assert
        run.ExitCode.Should().Be(0);
        File.Exists(ActionsFile).Should().BeFalse();
    }

    [Fact]
    public async Task Log_NotHookJson_ExitTwoOneLine()
    {
        // Act
        var run = await Log("nope");

        // Assert
        run.ExitCode.Should().Be(2);
        run.Stderr.Should().Be("m365-log: hook input is not a PostToolUse object with tool_name\n");
    }

    [Fact]
    public async Task Log_AnyArgument_ExitTwo()
    {
        // Act
        var run = await Log(Fixture("post-move-ok"), null, "extra");

        // Assert
        run.ExitCode.Should().Be(2);
        run.Stderr.Should().Be("m365-log: takes no arguments\n");
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "Unix file modes: Linux only")]
    [SupportedOSPlatform("linux")]
    public async Task Log_OnLinux_File0600StateDir0700()
    {
        // Act
        await Log(Fixture("post-move-ok"));

        // Assert
        File.GetUnixFileMode(ActionsFile).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.GetUnixFileMode(Path.GetDirectoryName(ActionsFile)!).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Fact]
    public async Task Log_StateDirUnwritable_ExitTwoOneLine()
    {
        // Arrange: a file where the state directory should be
        var env = _fixture.Env();
        var blocked = Path.Combine(_fixture.Root, "blocked");
        File.WriteAllText(blocked, "not a directory");
        env["ZYGGY_STATE_DIR"] = blocked;

        // Act
        var run = await Log(Fixture("post-move-ok"), env);

        // Assert
        run.ExitCode.Should().Be(2);
        run.Stderr.Should().StartWith("m365-log: could not append to ").And.EndWith("actions.jsonl\n");
        run.Stderr.Count(c => c == '\n').Should().Be(1);
    }

    [Fact]
    public async Task Log_TwoProcessesAtOnce_RowsIntact()
    {
        // Arrange
        var hook = Fixture("post-send-ok");

        // Act
        await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(
            async () =>
            {
                for (var i = 0; i < 20; i++)
                {
                    (await Log(hook)).ExitCode.Should().Be(0);
                }
            },
            TestContext.Current.CancellationToken)));

        // Assert
        var lines = File.ReadAllLines(ActionsFile);
        lines.Should().HaveCount(40);
        lines.Should().OnlyContain(l => JsonDocument.Parse(l, default).RootElement.GetProperty("status").GetString() == "ok");
    }
}
