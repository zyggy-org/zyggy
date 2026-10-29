using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Git;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Bus;

/// <summary>Spec 13 AC-5, AC-11..AC-13 (§4 as amended by A13-4..A13-6): Central lists what the other machines left for it and archives it in one commit.</summary>
public sealed class BusRepositoryArchiveTests : IDisposable
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly TenantId OtherOrg = TenantId.Parse("other-org");

    private static readonly MachineName Central = MachineName.Central;

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly MachineName WorkLaptop = MachineName.Parse("work-laptop");

    private static readonly BusRemote Remote = new("acme-org", "acme-bus", "main", new Uri("https://example.invalid/acme-org/acme-bus.git"), null);

    private static readonly DateTimeOffset T = new(2026, 10, 29, 10, 42, 7, TimeSpan.Zero);

    private static readonly DateTimeOffset Created = new(2026, 10, 29, 9, 0, 0, TimeSpan.Zero);

    private readonly TempCheckout _checkout = new(Acme, Central);

    private readonly RecordingProcessRunner _processes = new();

    private readonly IBusProvider _provider = Substitute.For<IBusProvider>();

    private readonly RecordingLogger<BusRepository> _log = new();

    private readonly FakeTimeProvider _clock = new(T);

    public BusRepositoryArchiveTests()
    {
        _provider.Remote.Returns(Remote);
        _provider.PullAsync(Arg.Any<CancellationToken>()).Returns(new BusSyncResult(true, string.Empty));
        _provider.PushAsync(Arg.Any<CancellationToken>()).Returns(new PushSucceeded());
    }

    public void Dispose() => _checkout.Dispose();

    [Fact]
    public async Task ListPickupCandidatesAsync_ContextAndReportsOfOtherMachines_OrdinalOrderWithBytes()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string homeContext = Drop(BusPaths.Context(Acme, HomeLaptop), "home context");
        string homeReport = Drop(BusPaths.Reports(Acme, HomeLaptop), "home report");
        string workContext = Drop(BusPaths.Context(Acme, WorkLaptop), "work context");
        _checkout.Drop(BusPaths.Context(Acme, HomeLaptop) + "/.gitkeep", []);

        // Act
        IReadOnlyList<PickupCandidate> candidates = await Repository().ListPickupCandidatesAsync(ct);

        // Assert
        candidates.Select(c => c.File.RelativePath).Should().Equal(homeContext, homeReport, workContext);
        candidates.Select(c => c.Folder).Should().Equal(HomeLaptop, HomeLaptop, WorkLaptop);
        candidates.Select(c => c.Kind).Should().Equal(BusFolder.Context, BusFolder.Reports, BusFolder.Context);
        candidates.Select(c => System.Text.Encoding.UTF8.GetString(c.Bytes)).Should().Equal("home context", "home report", "work context");
    }

    [Fact]
    public async Task ListPickupCandidatesAsync_CentralsOwnFolders_NeverListed()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Drop(BusPaths.Context(Acme, Central), "own context");
        Drop(BusPaths.Reports(Acme, Central), "own report");
        string laptop = Drop(BusPaths.Context(Acme, HomeLaptop), "laptop");

        // Act
        IReadOnlyList<PickupCandidate> candidates = await Repository().ListPickupCandidatesAsync(ct);

        // Assert
        candidates.Should().ContainSingle().Which.File.RelativePath.Should().Be(laptop);
    }

    [Fact]
    public async Task ListPickupCandidatesAsync_JobsClaimedRejectedRegistryArchive_NeverListed()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Drop(BusPaths.Jobs(Acme, HomeLaptop), "job");
        Drop(BusPaths.Claimed(Acme, HomeLaptop), "claimed");
        Drop(BusPaths.Rejected(Acme, HomeLaptop), "rejected");
        Drop(BusPaths.Archive(Acme, 2026, 10), "archived");
        _checkout.Drop(BusPaths.Registry(Acme, HomeLaptop), "{}"u8.ToArray());

        // Act
        IReadOnlyList<PickupCandidate> candidates = await Repository().ListPickupCandidatesAsync(ct);

        // Assert
        candidates.Should().BeEmpty();
    }

    [Fact]
    public async Task ListPickupCandidatesAsync_OtherTenantSubtree_NeverOpened()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Drop(BusPaths.Context(OtherOrg, HomeLaptop), "other tenant");
        string other = BusPaths.Resolve(_checkout.Root, BusPaths.Tenant(OtherOrg));
        var before = TreeFingerprint.Of(other);

        // Act
        IReadOnlyList<PickupCandidate> candidates = await Repository().ListPickupCandidatesAsync(ct);

        // Assert
        candidates.Should().BeEmpty();
        TreeFingerprint.Of(other).Should().Equal(before);
    }

    [Fact]
    public async Task ListPickupCandidatesAsync_LockHeld_ThrowsBusBusy()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Path.Combine(_checkout.Root, ".git"));
        using IDisposable? other = await BusCheckoutLock.AcquireAsync(_checkout.Root, BusCheckoutLock.WriteLockName, TimeSpan.Zero, _clock, ct);

        // Act
        Task<IReadOnlyList<PickupCandidate>> task = Repository().ListPickupCandidatesAsync(ct);
        for (int i = 0; i < 200 && !task.IsCompleted; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(5, ct);
        }

        Func<Task> act = () => task;

        // Assert
        await act.Should().ThrowAsync<BusBusyException>();
    }

    [Fact]
    public async Task ArchiveAsync_TwoContextsOneReport_OneMvEachOneCommitSubjectAndBodyPushed()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var files = new[]
        {
            (File: File(BusPaths.Context(Acme, HomeLaptop)), Created),
            (File: File(BusPaths.Reports(Acme, HomeLaptop)), Created),
            (File: File(BusPaths.Context(Acme, WorkLaptop)), Created),
        };
        string[] tos = [.. files.Select(f => ArchivePath(f.File.Id, 2026, 10))];

        // Act
        WriteOutcome outcome = await Repository().ArchiveAsync(files, ct);

        // Assert
        outcome.Should().Be(new Pushed(1));
        _processes.CallsOf("mv").Select(c => c.Arguments.ToArray()).Should().BeEquivalentTo(
            files.Select((f, i) => new[] { "mv", "--", f.File.RelativePath, tos[i] }), o => o.WithStrictOrdering());
        var commit = _processes.CallsOf("commit").Should().ContainSingle().Subject;
        commit.StandardInput.Should().Be(
            "acme archive 3 central→archive\n\n"
            + string.Join('\n', files.Select((f, i) => f.File.RelativePath + " → " + tos[i])));
        commit.Arguments.Should().EndWith([.. files.Select(f => f.File.RelativePath), .. tos]);
    }

    [Fact]
    public async Task ArchiveAsync_ArchiveMonthIsCreatedMonthNotTodays()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        _clock.SetUtcNow(new DateTimeOffset(2026, 11, 2, 8, 0, 0, TimeSpan.Zero));
        BusFile file = File(BusPaths.Context(Acme, HomeLaptop));

        // Act
        await Repository().ArchiveAsync([(file, new DateTimeOffset(2026, 10, 31, 23, 59, 59, TimeSpan.Zero))], ct);

        // Assert
        _processes.CallsOf("mv").Should().ContainSingle().Which.Arguments[^1].Should().Be(ArchivePath(file.Id, 2026, 10));
    }

    [Fact]
    public async Task ArchiveAsync_CreatedInTheFuture_ThatMonthsFolder()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        BusFile file = File(BusPaths.Context(Acme, HomeLaptop));

        // Act
        await Repository().ArchiveAsync([(file, new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero))], ct);

        // Assert
        _processes.CallsOf("mv").Should().ContainSingle().Which.Arguments[^1].Should().Be(ArchivePath(file.Id, 2026, 12));
        Directory.Exists(BusPaths.Resolve(_checkout.Root, BusPaths.Archive(Acme, 2026, 12))).Should().BeTrue();
    }

    [Fact]
    public async Task ArchiveAsync_PullFails_WriteFailedPull_NoMvNoCommit()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        _provider.PullAsync(Arg.Any<CancellationToken>()).Returns(new BusSyncResult(false, "could not resolve host"));

        // Act
        WriteOutcome outcome = await Repository().ArchiveAsync([(File(BusPaths.Context(Acme, HomeLaptop)), Created)], ct);

        // Assert
        outcome.Should().BeOfType<WriteFailed>().Which.Stage.Should().Be("pull");
        _processes.CallsOf("mv").Should().BeEmpty();
        _processes.CallsOf("commit").Should().BeEmpty();
    }

    [Fact]
    public async Task ArchiveAsync_MvFails_WriteFailedMv_CheckoutRestored_NoCommit()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        _processes.On("mv", RecordingProcessRunner.Ok(), RecordingProcessRunner.Fail(128, "fatal: bad source"));
        BusFile first = File(BusPaths.Context(Acme, HomeLaptop));
        BusFile second = File(BusPaths.Context(Acme, WorkLaptop));

        // Act
        WriteOutcome outcome = await Repository().ArchiveAsync([(first, Created), (second, Created)], ct);

        // Assert
        outcome.Should().Be(new WriteFailed("mv", "fatal: bad source"));
        _processes.CallsOf("mv").Should().HaveCount(2);
        _processes.CallsOf("commit").Should().BeEmpty();
        _processes.CallsOf("checkout").SelectMany(c => c.Arguments).Should().Contain([first.RelativePath, second.RelativePath, ArchivePath(first.Id, 2026, 10)]);
    }

    [Fact]
    public async Task ArchiveAsync_LockHeld_WriteFailedLock()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Path.Combine(_checkout.Root, ".git"));
        using IDisposable? other = await BusCheckoutLock.AcquireAsync(_checkout.Root, BusCheckoutLock.WriteLockName, TimeSpan.Zero, _clock, ct);

        // Act
        Task<WriteOutcome> task = Repository().ArchiveAsync([(File(BusPaths.Context(Acme, HomeLaptop)), Created)], ct);
        for (int i = 0; i < 200 && !task.IsCompleted; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(5, ct);
        }

        WriteOutcome outcome = await task;

        // Assert
        outcome.Should().BeOfType<WriteFailed>().Which.Stage.Should().Be("lock");
        _processes.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ArchiveAsync_EveryPushRejected_DeferredAttemptsFour_CommitLocal()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        _provider.PushAsync(Arg.Any<CancellationToken>()).Returns(new Rejected("fetch first"));

        // Act
        Task<WriteOutcome> task = Repository().ArchiveAsync([(File(BusPaths.Context(Acme, HomeLaptop)), Created)], ct);
        for (int i = 0; i < 200 && !task.IsCompleted; i++)
        {
            _clock.Advance(TimeSpan.FromMilliseconds(500));
            await Task.Delay(2, ct);
        }

        WriteOutcome outcome = await task;

        // Assert
        outcome.Should().Be(new Deferred(4, "fetch first"));
        _processes.CallsOf("commit").Should().ContainSingle();
        _processes.CallsOf("checkout").Should().BeEmpty();
    }

    [Fact]
    public async Task ArchiveAsync_NotCentral_ThrowsWriteRule_NothingCalled()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;

        // Act
        Func<Task> act = () => Repository(HomeLaptop).ArchiveAsync([(File(BusPaths.Context(Acme, HomeLaptop)), Created)], ct);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("write rule: only central archives");
        _processes.Calls.Should().BeEmpty();
        await _provider.DidNotReceive().PullAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("jobs")]
    [InlineData("claimed")]
    public async Task ArchiveAsync_SourceInJobsOrClaimed_ThrowsWriteRule(string folder)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        BusFile file = File(folder == "jobs" ? BusPaths.Jobs(Acme, HomeLaptop) : BusPaths.Claimed(Acme, HomeLaptop));

        // Act
        Func<Task> act = () => Repository().ArchiveAsync([(file, Created)], ct);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("write rule:*");
        _processes.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ArchiveAsync_SourceInAnotherTenant_ThrowsWriteRule()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        BusFile file = File(BusPaths.Context(OtherOrg, HomeLaptop));

        // Act
        Func<Task> act = () => Repository().ArchiveAsync([(file, Created)], ct);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("write rule:*another tenant");
        _processes.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ArchiveAsync_EmptyList_ThrowsArgument()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;

        // Act
        Func<Task> act = () => Repository().ArchiveAsync([], ct);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ArchiveAsync_CentralsOwnReport_Allowed()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        BusFile file = File(BusPaths.Reports(Acme, Central));

        // Act
        WriteOutcome outcome = await Repository().ArchiveAsync([(file, Created)], ct);

        // Assert
        outcome.Should().Be(new Pushed(1));
        _processes.CallsOf("mv").Should().ContainSingle().Which.Arguments[^1].Should().Be(ArchivePath(file.Id, 2026, 10));
    }

    private static string ArchivePath(EnvelopeId id, int year, int month) => BusPaths.EnvelopeFile(BusPaths.Archive(Acme, year, month), id);

    private string Drop(string folder, string text)
    {
        string path = BusPaths.EnvelopeFile(folder, EnvelopeId.New(_clock.GetUtcNow()));
        _checkout.Drop(path, System.Text.Encoding.UTF8.GetBytes(text));
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        return path;
    }

    private BusFile File(string folder)
    {
        var id = EnvelopeId.New(_clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        return new BusFile(BusPaths.EnvelopeFile(folder, id), id);
    }

    private BusRepository Repository(MachineName? machine = null)
    {
        MachineName me = machine ?? Central;
        var git = new GitClient(_processes, new GitClientOptions { Identity = new GitIdentity("zyggy (" + me.Value + ")", me.Value + "@zyggy.org") }, _clock);
        return new BusRepository(git, _provider, new BusIdentity(Acme, me), _checkout.Root, new EnvelopeSigner(new Zyggy.Core.Secrets.InMemorySecretStore()), _clock, () => 0.5, _log);
    }
}
