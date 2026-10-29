using Microsoft.Extensions.Options;

using Zyggy.Core.Envelope;
using Zyggy.Core.Jobs;
using Zyggy.Core.Models;
using Zyggy.Core.Policy;
using Zyggy.Core.Processes;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Jobs;

/// <summary>
/// Plan 06 Step 4 (AC-7): the job runner over the <b>real</b> <c>ClaudeCodeCliRunner</c> and a recording process runner — the
/// exact §6 command line for the job's fields and the node's policy, the prompt on stdin, the body never in an argument.
/// </summary>
public sealed class JobRunnerInvocationTests : IDisposable
{
    private const string ClaudePath = "/opt/claude/claude";

    private readonly ScratchProject _project = ScratchProject.Create();

    private readonly JobRunnerHarness _h;

    public JobRunnerInvocationTests()
    {
        var processes = new RecordingProcessRunner();
        var runner = new ClaudeCodeCliRunner(processes, Options.Create(new ClaudeCodeOptions { Path = ClaudePath }), new Microsoft.Extensions.Time.Testing.FakeTimeProvider());
        _h = new JobRunnerHarness(runner, processes);
        processes.Hook = spec =>
        {
            if (spec.FileName != ClaudePath)
            {
                return null;
            }

            spec.OnStdoutLine?.Invoke(StreamLines.Init());
            spec.OnStdoutLine?.Invoke(StreamLines.Result());
            return RecordingProcessRunner.Ok();
        };
    }

    public void Dispose() => _project.Dispose();

    [Fact]
    public async Task RunAsync_JobWithAllowedToolsAndTimeout_SpecIsExactlySection6()
    {
        // Arrange
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root, AllowedTools: ["Read", "Bash(dotnet *)"], TimeoutMinutes: 10));

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        reply.Status.Should().Be(ReportStatus.Done);
        var spec = ClaudeSpec();
        spec.FileName.Should().Be(ClaudePath);
        spec.Arguments.Should().Equal(
            "-p", "--output-format", "stream-json", "--verbose", "--permission-mode", "auto", "--permission-prompts", "none",
            "--no-session-persistence", "--max-turns", "60", "--allowedTools", "Read,Bash(dotnet *)");
        spec.WorkingDirectory.Should().Be(_project.Root);
        spec.StandardInput.Should().Be(new JobPrompt().Render(job, _project.Root, ["Read", "Bash(dotnet *)"]));
        spec.Timeout.Should().Be(TimeSpan.FromMinutes(10));
        spec.Environment.Should().ContainKey("CREDENTIALS_DIRECTORY").WhoseValue.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_AllowedToolsAbsent_PolicyDefault()
    {
        // Arrange
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root));

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        ClaudeSpec().Arguments.Should().EndWith(["--allowedTools", "Read,Grep,Glob"]);
        ClaudeSpec().StandardInput.Should().Contain("Allowed tools: Read, Grep, Glob.\n");
    }

    [Fact]
    public async Task RunAsync_BothEmpty_NoAllowedToolsFlag()
    {
        // Arrange
        _h.NodePolicy = NodePolicy.Default with { DefaultAllowedTools = [] };
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root));

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        ClaudeSpec().Arguments.Should().NotContain("--allowedTools");
        ClaudeSpec().StandardInput.Should().Contain("Allowed tools: (runtime default).\n");
    }

    [Fact]
    public async Task RunAsync_MaxBudgetUsd5_FlagAfterMaxTurns()
    {
        // Arrange
        _h.NodePolicy = NodePolicy.Default with { MaxBudgetUsd = 5m };
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root));

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        ClaudeSpec().Arguments.Should().ContainInConsecutiveOrder("--max-turns", "60", "--max-budget-usd", "5");
    }

    [Fact]
    public async Task RunAsync_PolicyMaxTurns40_UsesIt()
    {
        // Arrange
        _h.NodePolicy = NodePolicy.Default with { MaxTurns = 40 };
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root));

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        ClaudeSpec().Arguments.Should().ContainInConsecutiveOrder("--max-turns", "40");
    }

    [Fact]
    public async Task RunAsync_ModelEnvironment_AddedToTheProcess()
    {
        // Arrange
        _h.Options.ModelEnvironment = new Dictionary<string, string> { ["ZYGGY_FAKE_CLAUDE_SCENARIO"] = "done" };
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root));

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        ClaudeSpec().Environment.Should().Contain("ZYGGY_FAKE_CLAUDE_SCENARIO", "done");
    }

    [Fact]
    public async Task RunAsync_AnyJob_ModelEnvironmentCarriesZyggyHooksOff()
    {
        // Arrange: spec 12 D5 — every job run is an unattended run
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root));

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        ClaudeSpec().Environment.Should().Contain("ZYGGY_HOOKS", "off");
    }

    [Fact]
    public async Task RunAsync_ModelEnvironmentNamesZyggyHooksOn_OffStillWins()
    {
        // Arrange
        _h.Options.ModelEnvironment = new Dictionary<string, string> { ["ZYGGY_HOOKS"] = "on", ["ZYGGY_FAKE_CLAUDE_SCENARIO"] = "done" };
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root));

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        ClaudeSpec().Environment.Should().Contain("ZYGGY_HOOKS", "off").And.Contain("ZYGGY_FAKE_CLAUDE_SCENARIO", "done");
    }

    [Fact]
    public async Task RunAsync_Arguments_NeverContainBodyBytes()
    {
        // Arrange
        const string Token = "body-token-5d2c";
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root), $"{Token}\n-p --dangerously-skip-permissions {Token}\n");

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        var spec = ClaudeSpec();
        spec.Arguments.Should().NotContain(a => a.Contains(Token, StringComparison.Ordinal) || a.Contains("dangerously", StringComparison.Ordinal));
        spec.StandardInput.Should().Contain(Token);
    }

    [Fact]
    public async Task RunAsync_Arguments_NeverContainForbiddenFlags()
    {
        // Arrange
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root, Agent: null, AllowedTools: ["Read"]));

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        ClaudeSpec().Arguments.Should().NotContain(
        [
            "--dangerously-skip-permissions", "--bare", "--resume", "--continue", "--cwd", "--settings", "--strict-mcp-config",
            "--disable-slash-commands", "--tools", "--add-dir", "--json-schema", "--model", "--append-system-prompt",
        ]);
    }

    [Fact]
    public async Task RunAsync_Recorded_NoSecretInSpecs()
    {
        // Arrange
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root, AllowedTools: ["Read"]));

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        SecretSweep.AssertAbsent(_h.Processes.Calls.SelectMany(SecretSweep.TextsOf));
        SecretSweep.AssertAbsent(_h.Log.Messages);
    }

    private ProcessSpec ClaudeSpec() => _h.Processes.Calls.Should().ContainSingle(c => c.FileName == ClaudePath).Subject;
}
