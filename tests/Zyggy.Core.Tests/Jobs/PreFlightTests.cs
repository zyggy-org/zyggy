using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Envelope;
using Zyggy.Core.Git;
using Zyggy.Core.Jobs;
using Zyggy.Core.Runs;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Jobs;

/// <summary>Plan 06 Step 1: the §6 pre-flight edge cases (AC-2, AC-3).</summary>
public sealed class PreFlightTests : IDisposable
{
    private readonly ScratchProject _project = ScratchProject.Create();

    private readonly RecordingProcessRunner _processes = new();

    public void Dispose() => _project.Dispose();

    [Fact]
    public async Task CheckAsync_TrailingSeparatorAndForwardSlashes_NormalisedPath()
    {
        // Arrange
        var given = _project.Root.Replace('\\', '/') + "/";

        // Act
        var result = await CheckAsync(new JobFields(given));

        // Assert
        result.Should().BeOfType<PreFlightResult.Ok>().Which.ProjectPath.Should().Be(_project.Root);
    }

    [Fact]
    public async Task CheckAsync_WorktreeGitFile_Accepted()
    {
        // Arrange
        Directory.Delete(Path.Combine(_project.Root, ".git"));
        File.WriteAllText(Path.Combine(_project.Root, ".git"), "gitdir: /elsewhere/.git/worktrees/x\n");

        // Act
        var result = await CheckAsync(new JobFields(_project.Root));

        // Assert
        result.Should().BeOfType<PreFlightResult.Ok>();
    }

    [Fact]
    public async Task CheckAsync_NoAgentsDirectoryNoAgent_Accepted()
    {
        // Act
        var result = await CheckAsync(new JobFields(_project.Root));

        // Assert
        result.Should().Be(new PreFlightResult.Ok(_project.Root, null));
    }

    [Fact]
    public async Task CheckAsync_AgentIsADirectory_UnknownAgent()
    {
        // Arrange
        Directory.CreateDirectory(Path.Combine(_project.Root, ".claude", "agents", "env-debugger.md"));

        // Act
        var result = await CheckAsync(new JobFields(_project.Root, Agent: "env-debugger"));

        // Assert
        result.Should().Be(new PreFlightResult.Rejected(RunFailureReason.UnknownAgent, "unknown agent"));
    }

    [Fact]
    public async Task CheckAsync_UnsafeAgentName_UnsafeAgentName()
    {
        // Act
        var result = await CheckAsync(new JobFields(_project.Root, Agent: "../../etc/passwd"));

        // Assert
        result.Should().Be(new PreFlightResult.Rejected(RunFailureReason.UnknownAgent, "unsafe agent name"));
    }

    [Fact]
    public async Task CheckAsync_RelativePath_NoGitInvocation()
    {
        // Act
        var result = await CheckAsync(new JobFields("src/x"));

        // Assert
        result.Should().Be(new PreFlightResult.Rejected(RunFailureReason.UnknownProject, "not absolute"));
        _processes.CallsOf("rev-parse").Should().BeEmpty();
    }

    [Fact]
    public async Task CheckAsync_RunsOnlyRevParseGitDir()
    {
        // Act
        await CheckAsync(new JobFields(_project.Root));

        // Assert
        var call = _processes.Calls.Should().ContainSingle().Subject;
        call.Arguments.Should().Equal("rev-parse", "--git-dir");
        call.WorkingDirectory.Should().Be(_project.Root);
    }

    private Task<PreFlightResult> CheckAsync(JobFields job) =>
        PreFlight.CheckAsync(job, new GitClient(_processes, new GitClientOptions(), new FakeTimeProvider()), CancellationToken.None);
}
