using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Processes;
using Zyggy.Core.Setup;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Setup;

/// <summary>Spec 42 AC-18 and plan 42 A6/R6: the five <c>zyggy-core</c> checkout states through a recorded <see cref="IProcessRunner"/>.</summary>
public sealed class CoreCheckoutTests : IDisposable
{
    private const string ShaA = "aaaaaaaaaaaa1111111111111111111111111111";
    private const string ShaB = "bbbbbbbbbbbb2222222222222222222222222222";

    private static readonly Uri Remote = new(SetupRequest.DefaultCoreRemote);

    private readonly SetupTestFiles _files = new();
    private readonly RecordingProcessRunner _processes = new();
    private readonly FakeTimeProvider _clock = new(SetupTestFiles.Now);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string CorePath => Path.Combine(_files.Root, "agents", "zyggy-core");

    public void Dispose() => _files.Dispose();

    [Fact]
    public async Task ApplyAsync_FolderAbsent_CloneBranchMainInParent_DoneWithSha()
    {
        // Arrange
        _processes.On("rev-parse", RecordingProcessRunner.Ok(ShaA + "\n"));

        // Act
        SetupLine line = await Checkout().ApplyAsync(CorePath, Remote, Ct);

        // Assert
        line.Should().Be(new SetupLine(SetupItem.ZyggyCore, SetupOutcome.Done, "cloned " + SetupRequest.DefaultCoreRemote + " at aaaaaaaaaaaa"));
        ProcessSpec clone = _processes.CallsOf("clone").Should().ContainSingle().Subject;
        clone.Arguments.Should().Equal("clone", "--branch", "main", SetupRequest.DefaultCoreRemote, Path.GetFullPath(CorePath));
        clone.WorkingDirectory.Should().Be(Path.GetDirectoryName(Path.GetFullPath(CorePath)));
        Directory.Exists(Path.GetDirectoryName(CorePath)).Should().BeTrue();
        _processes.CallsOf("rev-parse").Should().ContainSingle().Which.Arguments.Should().Equal("rev-parse", "HEAD");
    }

    [Fact]
    public async Task ApplyAsync_FolderEmpty_ClonedLikeAbsent()
    {
        // Arrange
        Directory.CreateDirectory(CorePath);

        // Act
        SetupLine line = await Checkout().ApplyAsync(CorePath, Remote, Ct);

        // Assert
        line.Outcome.Should().Be(SetupOutcome.Done);
        _processes.CallsOf("clone").Should().ContainSingle();
    }

    [Fact]
    public async Task ApplyAsync_CleanOnMain_PullFfOnly_AlreadyUpToDate_AlreadyDone()
    {
        // Arrange
        ExistingClone();
        _processes.On("rev-parse", TopLevel(), RecordingProcessRunner.Ok(ShaA + "\n"), RecordingProcessRunner.Ok(ShaA + "\n"));
        _processes.On("pull", RecordingProcessRunner.Ok("Already up to date.\n"));

        // Act
        SetupLine line = await Checkout().ApplyAsync(CorePath, Remote, Ct);

        // Assert
        line.Should().Be(new SetupLine(SetupItem.ZyggyCore, SetupOutcome.AlreadyDone, "already up to date (" + CorePath + ")"));
        ProcessSpec pull = _processes.CallsOf("pull").Should().ContainSingle().Subject;
        pull.Arguments.Should().Equal("pull", "--ff-only", "origin", "main");
        pull.WorkingDirectory.Should().Be(Path.GetFullPath(CorePath));
        _processes.CallsOf("clone").Should().BeEmpty();
    }

    [Fact]
    public async Task ApplyAsync_CleanOnMain_PullFfOnly_Advanced_DoneFastForwarded()
    {
        // Arrange
        ExistingClone();
        _processes.On("rev-parse", TopLevel(), RecordingProcessRunner.Ok(ShaA + "\n"), RecordingProcessRunner.Ok(ShaB + "\n"));
        _processes.On("pull", RecordingProcessRunner.Ok("Updating aaaaaaa..bbbbbbb\nFast-forward\n"));

        // Act
        SetupLine line = await Checkout().ApplyAsync(CorePath, Remote, Ct);

        // Assert
        line.Should().Be(new SetupLine(SetupItem.ZyggyCore, SetupOutcome.Done, "fast-forwarded to bbbbbbbbbbbb"));
    }

    [Theory]
    [InlineData("status", " M node/README.md\0", 0, "dirty working copy", false)]
    [InlineData("symbolic-ref", "feature/local\n", 0, "on branch feature/local", false)]
    [InlineData("symbolic-ref", "", 1, "detached HEAD", false)]
    [InlineData("pull", "hint: Diverging branches can't be fast-forwarded.\nfatal: Not possible to fast-forward, aborting.\n", 128, "diverged from origin/main", true)]
    public async Task ApplyAsync_DirtyOtherBranchOrDiverged_LeftAsIsWithTheReason(string subCommand, string output, int exitCode, string reason, bool pulled)
    {
        // Arrange
        ExistingClone();
        _processes.On(subCommand, exitCode == 0 ? RecordingProcessRunner.Ok(output) : RecordingProcessRunner.Fail(exitCode, output));

        // Act
        SetupLine line = await Checkout().ApplyAsync(CorePath, Remote, Ct);

        // Assert
        line.Should().Be(new SetupLine(SetupItem.ZyggyCore, SetupOutcome.LeftAsIs, reason));
        _processes.CallsOf("pull").Any().Should().Be(pulled);
        _processes.Calls.Select(RecordingProcessRunner.SubCommand).Should().NotContain(["reset", "stash", "checkout", "clean", "clone"]);
    }

    [Fact]
    public async Task ApplyAsync_NotACheckout_FailedUntouched()
    {
        // Arrange
        ExistingClone();
        _processes.On("rev-parse", RecordingProcessRunner.Fail(128, "fatal: not a git repository (or any of the parent directories): .git\n"));

        // Act
        SetupLine line = await Checkout().ApplyAsync(CorePath, Remote, Ct);

        // Assert
        line.Should().Be(new SetupLine(SetupItem.ZyggyCore, SetupOutcome.Failed, CorePath + " is not a git checkout"));
        _processes.Calls.Select(RecordingProcessRunner.SubCommand).Should().Equal("rev-parse");
    }

    [Fact]
    public async Task ApplyAsync_FolderInsideAnotherCheckout_FailedNotACheckout()
    {
        // Arrange
        ExistingClone();
        _processes.On("rev-parse", RecordingProcessRunner.Ok("C:/agents\nzyggy-core/\n"));

        // Act
        SetupLine line = await Checkout().ApplyAsync(CorePath, Remote, Ct);

        // Assert
        line.Should().Be(new SetupLine(SetupItem.ZyggyCore, SetupOutcome.Failed, CorePath + " is not a git checkout"));
        _processes.CallsOf("pull").Should().BeEmpty();
    }

    [Fact]
    public async Task ApplyAsync_OtherOrigin_FailedBothUrlsNamed()
    {
        // Arrange
        ExistingClone(RecordingProcessRunner.Ok("https://example.invalid/fork/zyggy-core.git\n"));

        // Act
        SetupLine line = await Checkout().ApplyAsync(CorePath, Remote, Ct);

        // Assert
        line.Should().Be(new SetupLine(SetupItem.ZyggyCore, SetupOutcome.Failed,
            CorePath + " has origin https://example.invalid/fork/zyggy-core.git, expected " + SetupRequest.DefaultCoreRemote));
        _processes.CallsOf("config").Should().ContainSingle().Which.Arguments.Should().Equal("config", "--get", "remote.origin.url");
        _processes.CallsOf("pull").Should().BeEmpty();
        _processes.CallsOf("clone").Should().BeEmpty();
    }

    [Fact]
    public async Task ApplyAsync_NoOrigin_FailedNamed()
    {
        // Arrange
        ExistingClone(RecordingProcessRunner.Fail(1, string.Empty));

        // Act
        SetupLine line = await Checkout().ApplyAsync(CorePath, Remote, Ct);

        // Assert
        line.Should().Be(new SetupLine(SetupItem.ZyggyCore, SetupOutcome.Failed, CorePath + " has no origin, expected " + SetupRequest.DefaultCoreRemote));
    }

    [Fact]
    public async Task ApplyAsync_RemoteUnreachable_FailedFirstStderrLine()
    {
        // Arrange
        _processes.On("clone", RecordingProcessRunner.Fail(128,
            "Cloning into 'zyggy-core'...\nfatal: unable to access '" + SetupRequest.DefaultCoreRemote + "/': Could not resolve host: example\nsecond line\n"));

        // Act
        SetupLine line = await Checkout().ApplyAsync(CorePath, Remote, Ct);

        // Assert
        line.Should().Be(new SetupLine(SetupItem.ZyggyCore, SetupOutcome.Failed,
            "fatal: unable to access '" + SetupRequest.DefaultCoreRemote + "/': Could not resolve host: example"));
        _processes.CallsOf("rev-parse").Should().BeEmpty();
    }

    [Fact]
    public async Task ApplyAsync_PullFailsOtherwise_FailedFirstStderrLine()
    {
        // Arrange
        ExistingClone();
        _processes.On("pull", RecordingProcessRunner.Fail(1, "From somewhere\nfatal: unable to access 'x': Proxy CONNECT aborted\n"));

        // Act
        SetupLine line = await Checkout().ApplyAsync(CorePath, Remote, Ct);

        // Assert
        line.Should().Be(new SetupLine(SetupItem.ZyggyCore, SetupOutcome.Failed, "fatal: unable to access 'x': Proxy CONNECT aborted"));
    }

    [Fact]
    public async Task ApplyAsync_GitCannotStart_FailedCouldNotBeStarted()
    {
        // Arrange
        _processes.On("clone", new ProcessResult(null, string.Empty, string.Empty, TimedOut: false, StartFailed: true, StdoutTruncated: false, TimeSpan.Zero));

        // Act
        SetupLine line = await Checkout().ApplyAsync(CorePath, Remote, Ct);

        // Assert
        line.Should().Be(new SetupLine(SetupItem.ZyggyCore, SetupOutcome.Failed, "git could not be started"));
    }

    [Fact]
    public async Task ApplyAsync_EveryGitCall_NoPromptVariablesAndNoCredentialSource()
    {
        // Arrange: a clone run, then a pull run over an existing folder; the isolation hook even tries to make GCM interactive
        var hook = new Dictionary<string, string?> { ["GIT_CONFIG_GLOBAL"] = "gitconfig", ["GCM_INTERACTIVE"] = "always", ["LC_ALL"] = "de_DE.UTF-8" };
        CoreCheckout checkout = CoreCheckout.Create(_processes, hook, _clock);
        await checkout.ApplyAsync(CorePath, Remote, Ct);
        ExistingClone();

        // Act
        await checkout.ApplyAsync(CorePath, Remote, Ct);

        // Assert
        _processes.Calls.Should().HaveCountGreaterThan(5);
        _processes.Calls.All(c =>
                c.FileName == "git"
                && c.Environment["GIT_TERMINAL_PROMPT"] == "0"
                && c.Environment["GCM_INTERACTIVE"] == "never"
                && c.Environment["LC_ALL"] == "C"
                && c.Environment["GIT_CONFIG_GLOBAL"] == "gitconfig"
                && !c.Environment.ContainsKey("GIT_ASKPASS")
                && !c.Arguments.Any(a => a.Contains("credential", StringComparison.OrdinalIgnoreCase)))
            .Should().BeTrue();
    }

    private static ProcessResult TopLevel() => RecordingProcessRunner.Ok("C:/agents/zyggy-core\n\n");

    private CoreCheckout Checkout() => CoreCheckout.Create(_processes, new Dictionary<string, string?>(), _clock);

    private void ExistingClone(ProcessResult? origin = null)
    {
        Directory.CreateDirectory(Path.Combine(CorePath, ".git"));
        _processes.On("config", origin ?? RecordingProcessRunner.Ok(SetupRequest.DefaultCoreRemote + "\n"));
    }
}
