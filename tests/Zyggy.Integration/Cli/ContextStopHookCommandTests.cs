using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Integration.Cli;

/// <summary>Spec 12 AC-13..AC-17: <c>zyggy context --stop-hook</c> in process spools the turn with nothing else happening — no process, no secret store, no checkout.</summary>
public sealed class ContextStopHookCommandTests : IAsyncLifetime
{
    private const string AwsKey = "AKIAABCDEFGHIJKLMNOP";

    private CliHarness _harness = null!;

    public async ValueTask InitializeAsync() => _harness = await CliHarness.CreateAsync(CancellationToken.None);

    public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

    private string SpoolDirectory => ContextSpool.DirectoryFor(_harness.NodeDirOf("home-laptop"), CliHarness.Acme);

    private string SpoolFile => Path.Combine(SpoolDirectory, HookJson.SessionId + ".md");

    private Task<(int Exit, VerbConsole Console)> StopHookAsync(string? stdin, string? nodeJson = null, IReadOnlyDictionary<string, string?>? env = null, bool withPatterns = true, CancellationToken ct = default) =>
        _harness.RunAsync(["context", "--stop-hook"], nodeJson ?? _harness.HomeLaptopNodeJson, stdin: stdin, extraEnvironment: env, more: s => s.AddSingleton<ISecretStore>(new ThrowingSecretStore()), withPatterns: withPatterns, ct: ct);

    [Fact]
    public async Task StopHook_FixtureJson_ExitZeroNothingOnStdout_OneLineSpooledUnderProjectScope_NoProcessNoStoreAccess()
    {
        // Act
        var ct = TestContext.Current.CancellationToken;
        var (exit, console) = await StopHookAsync(HookJson.Fixture, ct: ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        console.Stdout.Should().BeEmpty();
        console.Stderr.Should().BeEmpty();
        string[] lines = await File.ReadAllLinesAsync(SpoolFile, ct);
        lines.Should().HaveCount(2);
        lines[0].Should().Be("scope: project:project");
        lines[1].Should().MatchRegex(@"^- \[observed\] \d{4}-\d{2}-\d{2} \(project:project\) \[session 0b7c3d1e \d{2}:\d{2}Z\]: Done: the plan for deliverable 27 is written and reviewed\.$");
        _harness.Hooks.Calls.Should().BeEmpty("the hook path starts no process");
    }

    [Fact]
    public async Task StopHook_SecondTurnSameSession_SecondLineAppended()
    {
        // Act
        var ct = TestContext.Current.CancellationToken;
        await StopHookAsync(HookJson.Stop("first"), ct: ct);
        await StopHookAsync(HookJson.Stop("second"), ct: ct);

        // Assert
        string[] lines = await File.ReadAllLinesAsync(SpoolFile, ct);
        lines.Should().HaveCount(3);
        lines[1].Should().EndWith(": first");
        lines[2].Should().EndWith(": second");
    }

    [Theory]
    [InlineData(true, "note")]
    [InlineData(false, "")]
    public async Task StopHook_StopHookActiveOrEmptyMessage_ExitZeroNothingWrittenNoStderr(bool active, string message)
    {
        // Act
        var ct = TestContext.Current.CancellationToken;
        var (exit, console) = await StopHookAsync(HookJson.Stop(message, active: active), ct: ct);

        // Assert
        exit.Should().Be(0);
        console.Stdout.Should().BeEmpty();
        console.Stderr.Should().BeEmpty();
        Directory.Exists(SpoolDirectory).Should().BeFalse();
    }

    [Fact]
    public async Task StopHook_TerminalOnStdin_ExitZeroNothing()
    {
        // Act
        var ct = TestContext.Current.CancellationToken;
        var (exit, console) = await StopHookAsync(null, ct: ct);

        // Assert
        exit.Should().Be(0);
        console.Stdout.Should().BeEmpty();
        console.Stderr.Should().BeEmpty();
        Directory.Exists(SpoolDirectory).Should().BeFalse();
    }

    [Fact]
    public async Task StopHook_UnparseableStdin_ExitZeroOneStderrLine_NothingWritten()
    {
        // Act
        var ct = TestContext.Current.CancellationToken;
        var (exit, console) = await StopHookAsync("not json at all", ct: ct);

        // Assert
        exit.Should().Be(0);
        console.Stdout.Should().BeEmpty();
        console.Stderr.Should().Be("context: unparseable hook input\n");
        Directory.Exists(SpoolDirectory).Should().BeFalse();
    }

    [Fact]
    public async Task StopHook_SecretShapedFirstLine_ExitZeroStderrNamesPattern_NothingSpooled_TextNowhere()
    {
        // Act
        var ct = TestContext.Current.CancellationToken;
        var (exit, console) = await StopHookAsync(HookJson.Stop("Stored the key " + AwsKey + " in the vault."), ct: ct);

        // Assert
        exit.Should().Be(0);
        console.Stdout.Should().BeEmpty();
        console.Stderr.Should().Be("context: note refused (pattern aws-access-key)\n");
        Directory.Exists(SpoolDirectory).Should().BeFalse();
        Directory.EnumerateFiles(_harness.Scratch.Path, "*", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .Should().NotContain(t => t.Contains(AwsKey, StringComparison.Ordinal));
    }

    [Fact]
    public async Task StopHook_150LinesThenOneMore_MarkerOnceOneStderrLine_FurtherCallsNothing()
    {
        // Arrange: the spool pre-written with 150 hook lines
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(SpoolDirectory);
        await File.WriteAllTextAsync(SpoolFile, "scope: project:project\n" + string.Concat(Enumerable.Range(0, 150).Select(i => $"- [observed] 2026-10-28 (project:project) [session 0b7c3d1e 10:{i % 60:00}Z]: turn {i}\n")), ct);

        // Act
        var (exit, first) = await StopHookAsync(HookJson.Stop("turn 150"), ct: ct);
        var (secondExit, second) = await StopHookAsync(HookJson.Stop("turn 151"), ct: ct);

        // Assert
        exit.Should().Be(0);
        first.Stderr.Should().Be("context: cap of 150 hook lines reached for session 0b7c3d1e\n");
        secondExit.Should().Be(0);
        second.Stderr.Should().BeEmpty();
        string[] lines = await File.ReadAllLinesAsync(SpoolFile, ct);
        lines.Should().HaveCount(152);
        lines[^1].Should().Be("- [observed] cap reached: no further hook lines this session");
        lines.Should().NotContain(l => l.EndsWith("turn 150", StringComparison.Ordinal) || l.EndsWith("turn 151", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StopHook_HooksOff_ExitZeroNothing_EvenWithoutPatterns()
    {
        // Act
        var ct = TestContext.Current.CancellationToken;
        var env = new Dictionary<string, string?>(StringComparer.Ordinal) { ["ZYGGY_HOOKS"] = "off" };
        var (exit, console) = await StopHookAsync(HookJson.Fixture, env: env, withPatterns: false, ct: ct);

        // Assert
        exit.Should().Be(0);
        console.Stdout.Should().BeEmpty();
        console.Stderr.Should().BeEmpty();
        Directory.Exists(SpoolDirectory).Should().BeFalse();
    }

    [Fact]
    public async Task StopHook_PatternsUnresolvable_ExitThreeFailClosed_NothingSpooled()
    {
        // Act
        var ct = TestContext.Current.CancellationToken;
        var env = new Dictionary<string, string?>(StringComparer.Ordinal) { ["CLAUDE_PROJECT_DIR"] = null, ["ZYGGY_INSTANCE_DIR"] = null };
        var (exit, console) = await StopHookAsync(HookJson.Fixture, env: env, withPatterns: false, ct: ct);

        // Assert
        exit.Should().Be(3);
        console.Stderr.Should().Be("zyggy: configuration error: ZYGGY_SECRET_PATTERNS is not set (and no ZYGGY_INSTANCE_DIR or CLAUDE_PROJECT_DIR to derive it from)\n");
        Directory.Exists(SpoolDirectory).Should().BeFalse();
    }

    [Fact]
    public async Task StopHook_CentralNodeJson_ExitTwoUseMemoryRemember()
    {
        // Act
        var ct = TestContext.Current.CancellationToken;
        var (exit, console) = await StopHookAsync(HookJson.Fixture, nodeJson: _harness.CentralNodeJson, ct: ct);

        // Assert
        exit.Should().Be(2);
        console.Stderr.Should().Be("zyggy: to: this machine is central; use zyggy memory remember\n");
        _harness.Hooks.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task StopHook_NoBusCheckoutNeeded_WorksWhenLocalPathIsNotARepository()
    {
        // Arrange: a node.json whose bus.localPath is an empty directory — the hook path never checks the checkout (D2)
        var ct = TestContext.Current.CancellationToken;
        string notARepo = _harness.Scratch.File("not-a-repo");
        Directory.CreateDirectory(notARepo);
        string nodeJson = _harness.WriteNodeJson(CliHarness.HomeLaptop, notARepo, "laptop-norepo", nodeDir: _harness.NodeDirOf("home-laptop"));

        // Act
        var (exit, console) = await StopHookAsync(HookJson.Fixture, nodeJson: nodeJson, ct: ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        File.Exists(SpoolFile).Should().BeTrue();
        _harness.Hooks.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task StopHook_WindowsCwd_ProjectLowerCased()
    {
        // Act
        var ct = TestContext.Current.CancellationToken;
        var (exit, _) = await StopHookAsync(HookJson.Stop("note", cwd: "C:\\src\\Calizr"), ct: ct);

        // Assert
        exit.Should().Be(0);
        (await File.ReadAllLinesAsync(SpoolFile, ct))[0].Should().Be("scope: project:calizr");
    }
}
