using System.Diagnostics;
using System.Text;

using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Processes;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Processes;

public sealed class ProcessRunnerTests : IDisposable
{
    private readonly ScratchDirectory _scratch = new();
    private readonly ServiceProvider _provider = new ServiceCollection().AddProcessRunner().BuildServiceProvider();

    private IProcessRunner Runner => _provider.GetRequiredService<IProcessRunner>();

    public void Dispose()
    {
        _provider.Dispose();
        _scratch.Dispose();
    }

    private ProcessSpec Fake(IReadOnlyList<string> args, Dictionary<string, string?> env, string? stdin = null, int seconds = 30) =>
        new(FakeClaude.ExecutablePath, args, _scratch.Path)
        {
            Environment = env,
            StandardInput = stdin,
            Timeout = TimeSpan.FromSeconds(seconds),
        };

    [Fact]
    public async Task RunAsync_StdinGiven_ChildReceivesUtf8BytesWithoutBom()
    {
        // Arrange
        var stdinPath = _scratch.File("stdin.bin");
        const string Prompt = "Fait: café ☕ \"quoted\"\nline two\n";
        var spec = Fake(["-p"], new() { ["ZYGGY_FAKE_CLAUDE_STDIN_CAPTURE"] = stdinPath }, Prompt);

        // Act
        var result = await Runner.RunAsync(spec, TestContext.Current.CancellationToken);

        // Assert
        result.ExitCode.Should().Be(0);
        result.StartFailed.Should().BeFalse();
        File.ReadAllBytes(stdinPath).Should().Equal(new UTF8Encoding(false).GetBytes(Prompt));
    }

    [Fact]
    public async Task RunAsync_ArgumentsWithSpacesQuotesAndNewlines_ReachChildUnchanged()
    {
        // Arrange
        var capturePath = _scratch.File("args.bin");
        string[] args = ["-p", "two words", "a \"quoted\" value", "line one\nline two", "", "back\\slash\\"];
        var spec = Fake(args, new() { ["ZYGGY_FAKE_CLAUDE_CAPTURE"] = capturePath });

        // Act
        await Runner.RunAsync(spec, TestContext.Current.CancellationToken);

        // Assert
        FakeClaude.ReadCapture(capturePath).Arguments.Should().Equal(args);
    }

    [Fact]
    public async Task RunAsync_ChildExceedsTimeout_KillsTreeReportsTimedOutAndLeavesNoProcess()
    {
        // Arrange
        var spec = Fake(["-p"], new() { ["ZYGGY_FAKE_CLAUDE_DELAY_MS"] = "30000" }, seconds: 2);
        var watch = Stopwatch.StartNew();

        // Act
        var result = await Runner.RunAsync(spec, TestContext.Current.CancellationToken);

        // Assert
        result.TimedOut.Should().BeTrue();
        result.ExitCode.Should().BeNull();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20));
        result.Duration.Should().BeLessThan(TimeSpan.FromSeconds(20));
        ProcessProbe.ProcessesWithWorkingDirectory(_scratch.Path).Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_CallerCancels_KillsChildThenThrowsOperationCanceled()
    {
        // Arrange
        var spec = Fake(["-p"], new() { ["ZYGGY_FAKE_CLAUDE_DELAY_MS"] = "30000" });
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(1));

        // Act
        Func<Task> act = () => Runner.RunAsync(spec, cts.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        ProcessProbe.ProcessesWithWorkingDirectory(_scratch.Path).Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_MissingExecutable_ReturnsStartFailed()
    {
        // Arrange
        var spec = new ProcessSpec(_scratch.File("no-such-program"), [], _scratch.Path) { Timeout = TimeSpan.FromSeconds(5) };

        // Act
        var result = await Runner.RunAsync(spec, TestContext.Current.CancellationToken);

        // Assert
        result.StartFailed.Should().BeTrue();
        result.ExitCode.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_StdoutOverCap_TruncatesCaptureButStreamsEveryLine()
    {
        // Arrange
        var lines = new List<string>();
        var expected = File.ReadAllLines(Path.Combine(FakeClaude.ScenarioDirectory, "success-structured.jsonl"));
        var spec = Fake(["-p"], new() { ["ZYGGY_FAKE_CLAUDE_SCENARIO"] = "success-structured" }) with
        {
            MaxStdoutBytes = 100,
            OnStdoutLine = lines.Add,
        };

        // Act
        var result = await Runner.RunAsync(spec, TestContext.Current.CancellationToken);

        // Assert
        result.ExitCode.Should().Be(0);
        result.StdoutTruncated.Should().BeTrue();
        Encoding.UTF8.GetByteCount(result.Stdout).Should().BeLessThanOrEqualTo(100);
        lines.Should().Equal(expected);
    }

    [Fact]
    public async Task RunAsync_LargeStdinChildNeverReads_CompletesWithoutHanging()
    {
        // Arrange
        var spec = Fake(["-p"], [], new string('x', 200 * 1024), seconds: 20);

        // Act
        var result = await Runner.RunAsync(spec, TestContext.Current.CancellationToken);

        // Assert
        result.TimedOut.Should().BeFalse();
        result.ExitCode.Should().Be(0);
    }
}
