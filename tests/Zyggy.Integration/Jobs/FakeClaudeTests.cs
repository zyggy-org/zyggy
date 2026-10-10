using System.Diagnostics;
using System.Text;

using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Jobs;

public sealed class FakeClaudeTests
{
    /// <summary>The §6 argument vector; no --cwd, the real CLI has none (the run directory is the working directory).</summary>
    private static readonly string[] Section6Arguments =
    [
        "-p", "Line one \"quoted\"\n\tline two",
        "--output-format", "stream-json",
        "--permission-mode", "auto",
        "--allowedTools", "Read,Edit,Bash(git *)",
        "--max-turns", "60",
    ];

    [Fact]
    public void ExecutablePath_AfterBuild_PointsToExistingApphost()
    {
        // Arrange
        var expectedName = OperatingSystem.IsWindows() ? "fake-claude.exe" : "fake-claude";
        var output = AppContext.BaseDirectory;

        // Act
        var path = FakeClaude.ExecutablePath;

        // Assert
        Path.GetFileName(path).Should().Be(expectedName);
        File.Exists(path).Should().BeTrue();
        new[]
        {
            Path.Combine(output, "fake-claude.dll"),
            Path.Combine(output, "fake-claude.runtimeconfig.json"),
            Path.Combine(output, "scenarios", "done.jsonl"),
        }.Should().AllSatisfy(f => File.Exists(f).Should().BeTrue(f));
    }

    [Fact]
    public async Task Run_DoneScenario_StreamsScenarioBytesExitsZeroWithEmptyStderr()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var scratch = new ScratchDirectory();
        var expected = await File.ReadAllBytesAsync(Path.Combine(FakeClaude.ScenarioDirectory, "done.jsonl"), ct);

        // Act
        var result = await RunAsync("done", scratch.File("capture.bin"), Section6Arguments, scratch.Path, ct);

        // Assert
        result.ExitCode.Should().Be(0);
        result.Stderr.Should().BeEmpty();
        result.Stdout.Should().Equal(expected);
    }

    [Fact]
    public async Task Run_DoneScenario_CapturesWorkingDirectoryAndArgumentsByteExact()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var scratch = new ScratchDirectory();
        var capturePath = scratch.File("capture.bin");

        // Act
        await RunAsync("done", capturePath, Section6Arguments, scratch.Path, ct);

        // Assert
        var capture = FakeClaude.ReadCapture(capturePath);
        Path.GetFullPath(capture.WorkingDirectory).Should().Be(Path.GetFullPath(scratch.Path));
        capture.Arguments.Should().Equal(Section6Arguments);
    }

    [Fact]
    public async Task Run_ScenarioUnset_BehavesAsDone()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var scratch = new ScratchDirectory();
        var expected = await File.ReadAllBytesAsync(Path.Combine(FakeClaude.ScenarioDirectory, "done.jsonl"), ct);

        // Act
        var result = await RunAsync(scenario: null, scratch.File("capture.bin"), Section6Arguments, scratch.Path, ct);

        // Assert
        result.ExitCode.Should().Be(0);
        result.Stdout.Should().Equal(expected);
    }

    [Fact]
    public async Task Run_ScenarioDirSet_ResolvesScenarioThere()
    {
        // Arrange: plan 37 assumption A6 — a materialised scenario outside the executable's directory.
        var ct = TestContext.Current.CancellationToken;
        using var scratch = new ScratchDirectory();
        var dir = Path.Combine(scratch.Path, "scenarios");
        Directory.CreateDirectory(dir);
        var bytes = "{\"type\":\"result\",\"subtype\":\"success\",\"result\":\"materialised\"}\n"u8.ToArray();
        await File.WriteAllBytesAsync(Path.Combine(dir, "only-here.jsonl"), bytes, ct);

        // Act
        var result = await RunAsync("only-here", null, [], scratch.Path, ct, new Dictionary<string, string> { ["ZYGGY_FAKE_CLAUDE_SCENARIO_DIR"] = dir });

        // Assert
        result.ExitCode.Should().Be(0, result.Stderr);
        result.Stdout.Should().Equal(bytes);
    }

    [Fact]
    public async Task Run_ScenarioDirSetNameMissing_ExitsThree()
    {
        // Arrange: "done" exists next to the executable, but not in the given directory.
        var ct = TestContext.Current.CancellationToken;
        using var scratch = new ScratchDirectory();

        // Act
        var result = await RunAsync("done", null, [], scratch.Path, ct, new Dictionary<string, string> { ["ZYGGY_FAKE_CLAUDE_SCENARIO_DIR"] = scratch.Path });

        // Assert
        result.ExitCode.Should().Be(3);
        result.Stderr.Should().Contain("fake-claude: unknown scenario 'done'");
    }

    [Fact]
    public async Task Run_UnknownScenario_ExitsThreeWritesDiagnosticAndStillCaptures()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var scratch = new ScratchDirectory();
        var capturePath = scratch.File("capture.bin");

        // Act
        var result = await RunAsync("does-not-exist", capturePath, Section6Arguments, scratch.Path, ct);

        // Assert
        result.ExitCode.Should().Be(3);
        result.Stdout.Should().BeEmpty();
        result.Stderr.TrimEnd().Should().Be("fake-claude: unknown scenario 'does-not-exist'");
        FakeClaude.ReadCapture(capturePath).Arguments.Should().Equal(Section6Arguments);
    }

    [Fact]
    public async Task Run_CapturePathIsDirectory_ExitsFourWithDiagnostic()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var scratch = new ScratchDirectory();

        // Act
        var result = await RunAsync("done", scratch.Path, Section6Arguments, scratch.Path, ct);

        // Assert
        result.ExitCode.Should().Be(4);
        result.Stdout.Should().BeEmpty();
        result.Stderr.TrimEnd().Split('\n').Should().ContainSingle().Which.Should().StartWith("fake-claude:");
    }

    [Fact]
    public async Task Run_EmptyArgument_CapturedAsEmptyRecord()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var scratch = new ScratchDirectory();
        var capturePath = scratch.File("capture.bin");

        // Act
        await RunAsync("done", capturePath, ["-p", "", "--max-turns", "60"], scratch.Path, ct);

        // Assert
        FakeClaude.ReadCapture(capturePath).Arguments.Should().Equal("-p", "", "--max-turns", "60");
    }

    [Fact]
    public async Task Run_NoArguments_CaptureHoldsOnlyWorkingDirectory()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var scratch = new ScratchDirectory();
        var capturePath = scratch.File("capture.bin");

        // Act
        await RunAsync("done", capturePath, [], scratch.Path, ct);

        // Assert
        var capture = FakeClaude.ReadCapture(capturePath);
        capture.Arguments.Should().BeEmpty();
        Path.GetFullPath(capture.WorkingDirectory).Should().Be(Path.GetFullPath(scratch.Path));
    }

    [Fact]
    public async Task Run_TwoConcurrentInvocations_EachReadsItsOwnCapture()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var scratch = new ScratchDirectory();
        var (captureA, captureB) = (scratch.File("a.bin"), scratch.File("b.bin"));
        string[] argsA = ["-p", "first"];
        string[] argsB = ["-p", "second", "--max-turns", "1"];

        // Act
        await Task.WhenAll(
            RunAsync("done", captureA, argsA, scratch.Path, ct),
            RunAsync("done", captureB, argsB, scratch.Path, ct));

        // Assert
        FakeClaude.ReadCapture(captureA).Arguments.Should().Equal(argsA);
        FakeClaude.ReadCapture(captureB).Arguments.Should().Equal(argsB);
    }

    [Fact]
    public async Task Run_StdinCaptureSet_WritesStdinByteExact()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var scratch = new ScratchDirectory();
        var stdinPath = scratch.File("stdin.bin");
        const string Prompt = "Ligne é \"quoted\"\n\ttwo\n";

        // Act
        var result = await RunAsync(
            "done", null, ["-p"], scratch.Path, ct, new() { ["ZYGGY_FAKE_CLAUDE_STDIN_CAPTURE"] = stdinPath }, Prompt);

        // Assert
        result.ExitCode.Should().Be(0);
        (await File.ReadAllBytesAsync(stdinPath, ct)).Should().Equal(new UTF8Encoding(false).GetBytes(Prompt));
    }

    [Fact]
    public async Task Run_StdinCaptureUnset_NeverReadsStdin()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var scratch = new ScratchDirectory();

        // Act: stdin is left open and never written; a fake that read it would block until the test timeout.
        var result = await RunAsync("done", null, ["-p"], scratch.Path, ct, keepStdinOpen: true);

        // Assert
        result.ExitCode.Should().Be(0);
    }

    [Fact]
    public async Task Run_ExitSet_ExitsWithThatCodeAfterStreaming()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var scratch = new ScratchDirectory();
        var expected = await File.ReadAllBytesAsync(Path.Combine(FakeClaude.ScenarioDirectory, "error.jsonl"), ct);

        // Act
        var result = await RunAsync("error", null, ["-p"], scratch.Path, ct, new() { ["ZYGGY_FAKE_CLAUDE_EXIT"] = "1" });

        // Assert
        result.ExitCode.Should().Be(1);
        result.Stdout.Should().Equal(expected);
    }

    [Theory]
    [InlineData("ZYGGY_FAKE_CLAUDE_DELAY_MS", "soon")]
    [InlineData("ZYGGY_FAKE_CLAUDE_DELAY_MS", "-5")]
    [InlineData("ZYGGY_FAKE_CLAUDE_EXIT", "x")]
    public async Task Run_InvalidDelay_ExitsFiveWithDiagnostic(string name, string value)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var scratch = new ScratchDirectory();

        // Act
        var result = await RunAsync("done", null, ["-p"], scratch.Path, ct, new() { [name] = value });

        // Assert
        result.ExitCode.Should().Be(5);
        result.Stdout.Should().BeEmpty();
        result.Stderr.TrimEnd().Should().Be($"fake-claude: invalid {name} '{value}'");
    }

    private static async Task<(int ExitCode, byte[] Stdout, string Stderr)> RunAsync(
        string? scenario,
        string? capturePath,
        IReadOnlyList<string> args,
        string workingDirectory,
        CancellationToken ct,
        Dictionary<string, string>? env = null,
        string? stdin = null,
        bool keepStdinOpen = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = FakeClaude.ExecutablePath,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        SetOrRemove(startInfo, "ZYGGY_FAKE_CLAUDE_SCENARIO", scenario);
        SetOrRemove(startInfo, "ZYGGY_FAKE_CLAUDE_CAPTURE", capturePath);
        foreach (var (name, value) in env ?? [])
        {
            startInfo.Environment[name] = value;
        }

        using var process = Process.Start(startInfo)!;
        if (stdin is not null)
        {
            var bytes = new UTF8Encoding(false).GetBytes(stdin);
            await process.StandardInput.BaseStream.WriteAsync(bytes, ct);
        }

        if (!keepStdinOpen)
        {
            process.StandardInput.Close();
        }

        using var stdout = new MemoryStream();
        var stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(stdout, ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        await stdoutTask;
        return (process.ExitCode, stdout.ToArray(), await stderrTask);
    }

    private static void SetOrRemove(ProcessStartInfo startInfo, string name, string? value)
    {
        if (value is null)
        {
            startInfo.Environment.Remove(name);
        }
        else
        {
            startInfo.Environment[name] = value;
        }
    }
}
