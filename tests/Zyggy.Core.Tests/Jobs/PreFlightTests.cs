using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Envelope;
using Zyggy.Core.Git;
using Zyggy.Core.Jobs;
using Zyggy.Core.Registry;
using Zyggy.Core.Runs;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Core.Tests.Registry;

namespace Zyggy.Core.Tests.Jobs;

/// <summary>Plan 06 Step 1: the §6 pre-flight edge cases (AC-2, AC-3); plan 14 Step 9: names from the node's own scan, paths confined to <c>devRoots</c> (AC-17).</summary>
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

        // Assert: plan 14 Step 9 — `not absolute` retired; a non-rooted value is a name, and `src/x` is not one
        result.Should().Be(new PreFlightResult.Rejected(RunFailureReason.UnknownProject, "unknown name"));
        _processes.CallsOf("rev-parse").Should().BeEmpty();
    }

    public static TheoryData<string, string?> Resolutions() => new()
    {
        { "name", null },
        { "unknown", "unknown name" },
        { "Calizr", "unknown name" },
        { "path", null },
        { "sibling-prefix", "outside devRoots" },
        { "dot-dot", "outside devRoots" },
        { "empty-devroots", "outside devRoots" },
    };

    [Theory]
    [MemberData(nameof(Resolutions))]
    public async Task CheckAsync_Resolution(string kind, string? check)
    {
        // Arrange: the snapshot maps calizr to the scratch project; devRoots = the scratch parent plus a second root
        string parent = ScratchProject.ParentRoot;
        IReadOnlyList<string> devRoots = kind == "empty-devroots" ? [] : [parent, Path.Combine(Path.GetTempPath(), "zyggy-unit-other")];
        string project = kind switch
        {
            "name" => "calizr",
            "unknown" => "unknown",
            "Calizr" => "Calizr",
            "sibling-prefix" => Path.Combine(parent + "2", "x"),
            "dot-dot" => parent + Path.DirectorySeparatorChar + "x" + Path.DirectorySeparatorChar + "..",
            _ => _project.Root,
        };

        // Act
        var result = await CheckAsync(new JobFields(project), Snapshot(), devRoots);

        // Assert
        if (check is null)
        {
            result.Should().Be(new PreFlightResult.Ok(_project.Root, null));
        }
        else
        {
            result.Should().Be(new PreFlightResult.Rejected(RunFailureReason.UnknownProject, check));
            _processes.Calls.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task CheckAsync_DriveLetterCaseDiffers_OkOnWindowsOnly()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows paths compare case-insensitively; elsewhere they do not");

        // Act
        var result = await CheckAsync(new JobFields(_project.Root), Snapshot(), [ScratchProject.ParentRoot.ToUpperInvariant()]);

        // Assert
        result.Should().Be(new PreFlightResult.Ok(_project.Root, null));
    }

    [Fact]
    public async Task CheckAsync_CaseDiffers_OutsideDevRootsElsewhere()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows compares case-insensitively");

        // Act
        var result = await CheckAsync(new JobFields(_project.Root), Snapshot(), [ScratchProject.ParentRoot.ToUpperInvariant()]);

        // Assert
        result.Should().Be(new PreFlightResult.Rejected(RunFailureReason.UnknownProject, "outside devRoots"));
    }

    [Fact]
    public async Task CheckAsync_ResolvedFolderRemovedAfterScan_Missing()
    {
        // Arrange
        string gone = Path.Combine(ScratchProject.ParentRoot, Guid.NewGuid().ToString("N"));

        // Act
        var result = await CheckAsync(new JobFields("calizr"), Snapshot(gone), [ScratchProject.ParentRoot]);

        // Assert
        result.Should().Be(new PreFlightResult.Rejected(RunFailureReason.UnknownProject, "missing"));
    }

    [Fact]
    public async Task CheckAsync_ResolvedNotGit_NotAGitRepository()
    {
        // Arrange
        using var noGit = ScratchProject.WithoutGit();
        _processes.On("rev-parse", RecordingProcessRunner.Fail(128, "fatal: not a git repository"));

        // Act
        var result = await CheckAsync(new JobFields("calizr"), Snapshot(noGit.Root), [ScratchProject.ParentRoot]);

        // Assert
        result.Should().Be(new PreFlightResult.Rejected(RunFailureReason.UnknownProject, "not a git repository"));
    }

    [Fact]
    public async Task CheckAsync_ResolvedNoClaude_NoClaude()
    {
        // Arrange
        using var noClaude = ScratchProject.WithoutClaude();

        // Act
        var result = await CheckAsync(new JobFields("calizr"), Snapshot(noClaude.Root), [ScratchProject.ParentRoot]);

        // Assert
        result.Should().Be(new PreFlightResult.Rejected(RunFailureReason.UnknownProject, "no .claude"));
    }

    [Fact]
    public async Task CheckAsync_AgentCheckedUnderResolvedPath()
    {
        // Arrange
        _project.WithAgent("env-debugger");

        // Act
        var present = await CheckAsync(new JobFields("calizr", Agent: "env-debugger"), Snapshot(), [ScratchProject.ParentRoot]);
        var absent = await CheckAsync(new JobFields("calizr", Agent: "db-migrator"), Snapshot(), [ScratchProject.ParentRoot]);

        // Assert
        present.Should().Be(new PreFlightResult.Ok(_project.Root, "env-debugger"));
        absent.Should().Be(new PreFlightResult.Rejected(RunFailureReason.UnknownAgent, "unknown agent"));
    }

    [Fact]
    public async Task CheckAsync_NamesOnlySnapshot_UnknownName()
    {
        // Arrange: a `detail: names` scan carries no path
        var snapshot = RegistrySnapshotTests.With(new RegistryProject("calizr", null, null, [], [], null));

        // Act
        var result = await CheckAsync(new JobFields("calizr"), snapshot, [ScratchProject.ParentRoot]);

        // Assert
        result.Should().Be(new PreFlightResult.Rejected(RunFailureReason.UnknownProject, "unknown name"));
    }

    [Fact]
    public async Task CheckAsync_PathExactlyADevRoot_Accepted()
    {
        // Act: the root itself is "equal", not "under" — accepted
        var result = await CheckAsync(new JobFields(_project.Root), Snapshot(), [_project.Root + Path.DirectorySeparatorChar]);

        // Assert
        result.Should().Be(new PreFlightResult.Ok(_project.Root, null));
    }

    [Fact]
    public async Task CheckAsync_NameEqualsRelativeFolderOfCwd_SnapshotOnly()
    {
        // Arrange: a project-shaped folder `calizr` in the current directory is never consulted
        string decoy = Path.Combine(Environment.CurrentDirectory, "calizr");
        bool created = !Directory.Exists(decoy);
        Directory.CreateDirectory(Path.Combine(decoy, ".claude"));
        Directory.CreateDirectory(Path.Combine(decoy, ".git"));
        try
        {
            // Act
            var empty = await CheckAsync(new JobFields("calizr"), new RegistrySnapshot(), [ScratchProject.ParentRoot, Environment.CurrentDirectory]);
            var known = await CheckAsync(new JobFields("calizr"), Snapshot(), [ScratchProject.ParentRoot, Environment.CurrentDirectory]);

            // Assert
            empty.Should().Be(new PreFlightResult.Rejected(RunFailureReason.UnknownProject, "unknown name"));
            known.Should().Be(new PreFlightResult.Ok(_project.Root, null));
        }
        finally
        {
            if (created)
            {
                Directory.Delete(decoy, recursive: true);
            }
        }
    }

    private RegistrySnapshot Snapshot(string? path = null) =>
        RegistrySnapshotTests.With(new RegistryProject("calizr", (path ?? _project.Root).Replace('\\', '/'), "main", [], [], null));

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

    private Task<PreFlightResult> CheckAsync(JobFields job) => CheckAsync(job, new RegistrySnapshot(), [ScratchProject.ParentRoot]);

    private Task<PreFlightResult> CheckAsync(JobFields job, RegistrySnapshot registry, IReadOnlyList<string> devRoots) =>
        PreFlight.CheckAsync(job, new GitClient(_processes, new GitClientOptions(), new FakeTimeProvider()), registry, devRoots, CancellationToken.None);
}
