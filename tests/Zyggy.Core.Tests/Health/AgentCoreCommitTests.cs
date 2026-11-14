using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Git;
using Zyggy.Core.Health;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Health;

/// <summary>
/// Spec 15 AC-12: the node reads the <c>HEAD</c> of the <c>zyggy-core</c> checkout (<c>agentCore.path</c>) at start and at every discovery
/// interval; a missing folder or a non-repository gives <see langword="null"/> and one warning per change.
/// </summary>
public sealed class AgentCoreCommitTests : IDisposable
{
    private const string Head = "89abcdef0123456789abcdef0123456789abcdef";

    private const string Later = "fedcba9876543210fedcba9876543210fedcba98";

    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(60);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "zyggy-ut", Guid.NewGuid().ToString("N"));

    private readonly RecordingProcessRunner _processes = new();

    private readonly FakeTimeProvider _clock = new();

    private readonly RecordingLogger<AgentCoreCommit> _log = new();

    private string Checkout => Path.Combine(_root, "zyggy-core");

    private IEnumerable<string> Warnings => _log.AtLevel(LogLevel.Warning).Select(e => e.Message);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private AgentCoreCommit Sut() =>
        new(new GitClient(_processes, new GitClientOptions(), _clock), Checkout, Interval, _clock, _log);

    private void MakeCheckout() => Directory.CreateDirectory(Path.Combine(Checkout, ".git"));

    [Fact]
    public void Sha_BeforeTheFirstRead_Null()
    {
        // Act
        AgentCoreCommit sut = Sut();

        // Assert
        sut.Sha.Should().BeNull();
    }

    [Fact]
    public async Task RefreshAsync_Checkout_ShaIsItsHead()
    {
        // Arrange
        MakeCheckout();
        _processes.On("rev-parse", RecordingProcessRunner.Ok(Head + "\n"));
        AgentCoreCommit sut = Sut();

        // Act
        await sut.RefreshAsync(TestContext.Current.CancellationToken);

        // Assert
        sut.Sha.Should().Be(Head);
        ProcessSpecShould(_processes.Calls.Should().ContainSingle().Subject);
        Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task RefreshAsync_FolderMissing_NullOneWarningNoGit()
    {
        // Arrange
        AgentCoreCommit sut = Sut();

        // Act
        await sut.RefreshAsync(TestContext.Current.CancellationToken);

        // Assert
        sut.Sha.Should().BeNull();
        Warnings.Should().Equal($"agent-core: {Checkout} is not a git checkout");
        _processes.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task RefreshAsync_FolderWithoutGitEntry_NullOneWarningNoGit()
    {
        // Arrange: a plain folder inside some other repository must not report that repository's HEAD
        Directory.CreateDirectory(Checkout);
        AgentCoreCommit sut = Sut();

        // Act
        await sut.RefreshAsync(TestContext.Current.CancellationToken);

        // Assert
        sut.Sha.Should().BeNull();
        Warnings.Should().Equal($"agent-core: {Checkout} is not a git checkout");
        _processes.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("HEAD\n")]
    [InlineData("89abcdef\n")]
    public async Task RefreshAsync_GitAnswersNoSha_NullOneWarning(string stdout)
    {
        // Arrange
        MakeCheckout();
        _processes.On("rev-parse", RecordingProcessRunner.Ok(stdout));
        AgentCoreCommit sut = Sut();

        // Act
        await sut.RefreshAsync(TestContext.Current.CancellationToken);

        // Assert
        sut.Sha.Should().BeNull();
        Warnings.Should().Equal($"agent-core: {Checkout} is not a git checkout");
    }

    [Fact]
    public async Task RefreshAsync_GitFails_NullOneWarning()
    {
        // Arrange: an empty repository has no HEAD commit
        MakeCheckout();
        _processes.On("rev-parse", RecordingProcessRunner.Fail(1, string.Empty));
        AgentCoreCommit sut = Sut();

        // Act
        await sut.RefreshAsync(TestContext.Current.CancellationToken);

        // Assert
        sut.Sha.Should().BeNull();
        Warnings.Should().Equal($"agent-core: {Checkout} is not a git checkout");
    }

    [Fact]
    public async Task RefreshAsync_ProcessThrows_NullOneWarningNoThrow()
    {
        // Arrange: git is not installed
        MakeCheckout();
        _processes.Hook = _ => throw new InvalidOperationException("git not found");
        AgentCoreCommit sut = Sut();

        // Act
        await sut.RefreshAsync(TestContext.Current.CancellationToken);

        // Assert
        sut.Sha.Should().BeNull();
        Warnings.Should().Equal($"agent-core: {Checkout} is not a git checkout");
    }

    [Fact]
    public async Task RefreshAsync_NotACheckoutThreeTimes_WarnsOnce()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        AgentCoreCommit sut = Sut();
        await sut.RefreshAsync(ct);
        await sut.RefreshAsync(ct);

        // Act
        await sut.RefreshAsync(ct);

        // Assert
        Warnings.Should().ContainSingle();
    }

    [Fact]
    public async Task RefreshAsync_CheckoutRemoved_NullAndOneWarningForThatChange()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        MakeCheckout();
        _processes.On("rev-parse", RecordingProcessRunner.Ok(Head + "\n"));
        AgentCoreCommit sut = Sut();
        await sut.RefreshAsync(ct);
        Directory.Delete(Checkout, recursive: true);

        // Act
        await sut.RefreshAsync(ct);

        // Assert
        sut.Sha.Should().BeNull();
        Warnings.Should().Equal($"agent-core: {Checkout} is not a git checkout");
    }

    [Fact]
    public async Task RefreshAsync_GoneBackGone_WarnsOncePerChange()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        AgentCoreCommit sut = Sut();
        await sut.RefreshAsync(ct);
        MakeCheckout();
        _processes.On("rev-parse", RecordingProcessRunner.Ok(Head + "\n"));
        await sut.RefreshAsync(ct);
        Directory.Delete(Checkout, recursive: true);

        // Act
        await sut.RefreshAsync(ct);

        // Assert
        Warnings.Should().HaveCount(2);
    }

    [Fact]
    public async Task RefreshAsync_HeadMoved_ShaFollows()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        MakeCheckout();
        _processes.On("rev-parse", RecordingProcessRunner.Ok(Head + "\n"), RecordingProcessRunner.Ok(Later + "\n"));
        AgentCoreCommit sut = Sut();
        await sut.RefreshAsync(ct);

        // Act
        await sut.RefreshAsync(ct);

        // Assert
        sut.Sha.Should().Be(Later);
    }

    [Fact]
    public async Task RunAsync_ReadsAtStartThenAtEveryInterval()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        MakeCheckout();
        _processes.On("rev-parse", RecordingProcessRunner.Ok(Head + "\n"), RecordingProcessRunner.Ok(Later + "\n"));
        AgentCoreCommit sut = Sut();

        // Act
        Task run = sut.RunAsync(stop.Token);
        await UntilAsync(() => sut.Sha == Head, ct);
        _clock.Advance(Interval - TimeSpan.FromSeconds(1));
        await SettleAsync(ct);
        string? beforeTheTick = sut.Sha;
        _clock.Advance(TimeSpan.FromSeconds(1));
        await UntilAsync(() => sut.Sha == Later, ct);
        await stop.CancelAsync();
        await run;

        // Assert
        beforeTheTick.Should().Be(Head);
        _processes.Calls.Should().HaveCount(2);
    }

    private void ProcessSpecShould(Core.Processes.ProcessSpec call)
    {
        call.FileName.Should().Be("git");
        call.WorkingDirectory.Should().Be(Checkout);
        call.Arguments.Should().Equal("rev-parse", "--verify", "-q", "HEAD");
    }

    private static async Task UntilAsync(Func<bool> condition, CancellationToken ct)
    {
        for (int i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(5, ct);
        }

        condition().Should().BeTrue("the condition should hold within the wait");
    }

    private static async Task SettleAsync(CancellationToken ct)
    {
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(5, ct);
        }
    }
}
