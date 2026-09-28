using System.Diagnostics;

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

    private static async Task<(int ExitCode, byte[] Stdout, string Stderr)> RunAsync(
        string? scenario,
        string? capturePath,
        IReadOnlyList<string> args,
        string workingDirectory,
        CancellationToken ct)
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

        using var process = Process.Start(startInfo)!;
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

    /// <summary>A per-test directory under &lt;temp&gt;/zyggy-it/, deleted on dispose.</summary>
    private sealed class ScratchDirectory : IDisposable
    {
        public ScratchDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "zyggy-it", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Leaked temp directories are documented in the README.
            }
        }
    }
}
