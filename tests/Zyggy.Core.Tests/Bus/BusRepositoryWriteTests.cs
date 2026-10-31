using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Git;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Bus;

/// <summary>Spec 04 AC-13..AC-16: the §5 write sequence, the start-up rule and the write-rule guard over a recorded git.</summary>
public sealed class BusRepositoryWriteTests : IDisposable
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName Central = MachineName.Parse("central");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly BusIdentity Me = new(Acme, HomeLaptop);

    private static readonly BusRemote Remote = new("acme-org", "acme-bus", "main", new Uri("https://example.invalid/acme-org/acme-bus.git"), null);

    private static readonly DateTimeOffset T = new(2026, 9, 30, 10, 0, 0, TimeSpan.Zero);

    private readonly TempCheckout _checkout = new(Acme, HomeLaptop);

    private readonly RecordingProcessRunner _processes = new();

    private readonly IBusProvider _provider = Substitute.For<IBusProvider>();

    private readonly RecordingLogger<BusRepository> _log = new();

    private readonly FakeTimeProvider _clock = new(T);

    private double _jitter = 0.5;

    public BusRepositoryWriteTests()
    {
        _provider.Remote.Returns(Remote);
        _provider.PullAsync(Arg.Any<CancellationToken>()).Returns(new BusSyncResult(true, string.Empty));
        _provider.PushAsync(Arg.Any<CancellationToken>()).Returns(new PushSucceeded());
    }

    public void Dispose() => _checkout.Dispose();

    [Fact]
    public async Task ClaimAsync_PushRejectedTwice_RetriesAfterJitteredDelaysAndPushes()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        _provider.PushAsync(Arg.Any<CancellationToken>()).Returns(new Rejected("fetch first"), new Rejected("fetch first"), new PushSucceeded());
        AcceptedJob job = await JobAsync(ct);
        var calls = new List<string>();
        _provider.When(p => p.PullAsync(Arg.Any<CancellationToken>())).Do(_ => calls.Add("pull"));
        _provider.When(p => p.PushAsync(Arg.Any<CancellationToken>())).Do(_ => calls.Add("push"));
        var delays = new List<TimeSpan>();

        // Act
        Task<WriteOutcome> task = Repository().ClaimAsync(job, ct);
        DateTimeOffset last = T;
        while (!task.IsCompleted)
        {
            _clock.Advance(TimeSpan.FromMilliseconds(500));
            await Task.Delay(1, ct);
        }

        WriteOutcome outcome = await task;

        // Assert
        outcome.Should().Be(new Pushed(3));
        calls.Should().Equal("pull", "push", "pull", "push", "pull", "push");
        _processes.Calls.Select(RecordingProcessRunner.SubCommand).Should().Equal("mv", "commit");
        _clock.GetUtcNow().Should().BeOnOrAfter(T.AddSeconds(4), "two delays of 1 s + 0.5 × 2 s");
        _clock.GetUtcNow().Should().BeBefore(T.AddSeconds(5));
    }

    [Theory]
    [InlineData(0.0, 1)]
    [InlineData(1.0, 3)]
    public async Task ClaimAsync_JitterZeroAndOne_DelaysAreOneAndThreeSeconds(double jitter, int expectedSeconds)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        _jitter = jitter;
        _provider.PushAsync(Arg.Any<CancellationToken>()).Returns(new Rejected("fetch first"), new PushSucceeded());
        AcceptedJob job = await JobAsync(ct);

        // Act
        Task<WriteOutcome> task = Repository().ClaimAsync(job, ct);
        _clock.Advance(TimeSpan.FromSeconds(expectedSeconds) - TimeSpan.FromMilliseconds(1));
        await Task.Delay(20, ct);
        bool stillWaiting = !task.IsCompleted;
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        WriteOutcome outcome = await task.WaitAsync(TimeSpan.FromSeconds(5), ct);

        // Assert
        stillWaiting.Should().BeTrue();
        outcome.Should().Be(new Pushed(2));
    }

    [Fact]
    public async Task ClaimAsync_CommitMessageAndIdentity_MatchSpec()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        AcceptedJob job = await JobAsync(ct);

        // Act
        WriteOutcome outcome = await Repository().ClaimAsync(job, ct);

        // Assert
        outcome.Should().Be(new Pushed(1));
        var commit = _processes.CallsOf("commit").Should().ContainSingle().Subject;
        commit.StandardInput.Should().Be($"acme claim {job.File.Id} central→home-laptop");
        commit.Arguments.Should().StartWith(["-c", "user.name=zyggy (home-laptop)", "-c", "user.email=home-laptop@zyggy.org", "commit", "--only", "-F", "-", "--"]);
        commit.Arguments.Should().EndWith([job.File.RelativePath, BusPaths.EnvelopeFile(BusPaths.Claimed(Acme, HomeLaptop), job.File.Id)]);
    }

    [Fact]
    public async Task ClaimAsync_MovesWithGitMvNotCopy()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        AcceptedJob job = await JobAsync(ct);

        // Act
        await Repository().ClaimAsync(job, ct);

        // Assert
        _processes.CallsOf("mv").Should().ContainSingle().Which.Arguments.Should().Equal(
            "mv", "--", job.File.RelativePath, BusPaths.EnvelopeFile(BusPaths.Claimed(Acme, HomeLaptop), job.File.Id));
        _processes.CallsOf("add").Should().BeEmpty();
        _processes.CallsOf("rm").Should().BeEmpty();
    }

    [Fact]
    public async Task ClaimAsync_PushFailsFourTimes_DeferredWithoutResetAndOneErrorLog()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        _provider.PushAsync(Arg.Any<CancellationToken>()).Returns(new Rejected("fetch first"));
        AcceptedJob job = await JobAsync(ct);

        // Act
        Task<WriteOutcome> task = Repository().ClaimAsync(job, ct);
        while (!task.IsCompleted)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(1, ct);
        }

        WriteOutcome outcome = await task;

        // Assert
        outcome.Should().BeOfType<Deferred>().Which.Attempts.Should().Be(4);
        await _provider.Received(4).PushAsync(Arg.Any<CancellationToken>());
        _processes.Calls.Select(RecordingProcessRunner.SubCommand).Should().NotContain(["reset", "checkout"]);
        _log.AtLevel(LogLevel.Error).Should().ContainSingle();
    }

    [Fact]
    public async Task ClaimAsync_PullFails_WriteFailedPullNothingChanged()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        _provider.PullAsync(Arg.Any<CancellationToken>()).Returns(new BusSyncResult(false, "could not resolve host"));
        AcceptedJob job = await JobAsync(ct);

        // Act
        WriteOutcome outcome = await Repository().ClaimAsync(job, ct);

        // Assert
        outcome.Should().Be(new WriteFailed("pull", "could not resolve host"));
        _processes.Calls.Should().BeEmpty();
        await _provider.DidNotReceive().PushAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ClaimAsync_MvFails_WriteFailedMvAndPathsRestored()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        _processes.On("mv", RecordingProcessRunner.Fail(128, "fatal: bad source"));
        AcceptedJob job = await JobAsync(ct);

        // Act
        WriteOutcome outcome = await Repository().ClaimAsync(job, ct);

        // Assert
        outcome.Should().Be(new WriteFailed("mv", "fatal: bad source"));
        _processes.Calls.Select(RecordingProcessRunner.SubCommand).Should().Equal("mv", "checkout", "checkout");
        _processes.CallsOf("commit").Should().BeEmpty();
        await _provider.DidNotReceive().PushAsync(Arg.Any<CancellationToken>());
        _log.AtLevel(LogLevel.Error).Should().ContainSingle();
    }

    [Fact]
    public async Task EnsureCleanAsync_DirtyTree_StashesWithNamedStashAndWarnsOnce()
    {
        // Arrange
        _processes.On("status", RecordingProcessRunner.Ok(" M tenants/acme/nodes/home-laptop/jobs/x.md\0?? stray.txt\0"));

        // Act
        BusStartupResult result = await Repository().EnsureCleanAsync(TestContext.Current.CancellationToken);

        // Assert
        result.Should().Be(BusStartupResult.Stashed);
        _processes.CallsOf("stash").Should().ContainSingle().Which.Arguments.Should().Equal(
            "stash", "push", "--include-untracked", "-m", "zyggy-home-laptop-20260930T100000Z");
        _log.AtLevel(LogLevel.Warning).Should().ContainSingle();
    }

    [Fact]
    public async Task EnsureCleanAsync_CleanTree_NoStashNoLog()
    {
        // Act
        BusStartupResult result = await Repository().EnsureCleanAsync(TestContext.Current.CancellationToken);

        // Assert
        result.Should().Be(BusStartupResult.Clean);
        _processes.CallsOf("stash").Should().BeEmpty();
        _log.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task EnsureCleanAsync_OnDev_WrongBranchNothingStashed()
    {
        // Arrange
        _processes.On("symbolic-ref", RecordingProcessRunner.Ok("dev\n"));
        _processes.On("status", RecordingProcessRunner.Ok("?? stray.txt\0"));

        // Act
        BusStartupResult result = await Repository().EnsureCleanAsync(TestContext.Current.CancellationToken);

        // Assert
        result.Should().Be(BusStartupResult.WrongBranch);
        _processes.CallsOf("stash").Should().BeEmpty();
    }

    [Fact]
    public async Task EnsureCleanAsync_RebaseMergeDirExists_RebaseInProgress()
    {
        // Arrange: rev-parse --git-dir first, then --git-path naming a directory that exists (the checkout itself)
        _processes.On("rev-parse", RecordingProcessRunner.Ok(".git\n"), RecordingProcessRunner.Ok(".\n"));

        // Act
        BusStartupResult result = await Repository().EnsureCleanAsync(TestContext.Current.CancellationToken);

        // Assert
        result.Should().Be(BusStartupResult.RebaseInProgress);
        _processes.CallsOf("stash").Should().BeEmpty();
    }

    [Fact]
    public async Task EnsureCleanAsync_NotARepository_WhenRevParseFails()
    {
        // Arrange
        _processes.On("rev-parse", RecordingProcessRunner.Fail(128, "fatal: not a git repository"));

        // Act
        BusStartupResult result = await Repository().EnsureCleanAsync(TestContext.Current.CancellationToken);

        // Assert
        result.Should().Be(BusStartupResult.NotARepository);
    }

    [Fact]
    public async Task EnsureCleanAsync_NoTenantDirectory_NoTenantDirectory()
    {
        // Arrange: a repository laid out for another tenant only
        using var other = new TempCheckout(TenantId.Parse("globex"), HomeLaptop);

        // Act
        BusStartupResult result = await Repository(other.Root).EnsureCleanAsync(TestContext.Current.CancellationToken);

        // Assert
        result.Should().Be(BusStartupResult.NoTenantDirectory);
    }

    [Theory]
    [InlineData("tenants/acme/nodes/central/jobs/{id}.md")]
    [InlineData("tenants/globex/nodes/home-laptop/jobs/{id}.md")]
    [InlineData("tenants/acme/archive/2026-09/{id}.md")]
    [InlineData("tenants/acme/registry/home-laptop.yaml")]
    [InlineData("tenants/acme/nodes/home-laptop/reports/{id}.md")]
    public async Task Write_OutsideMyInbox_ThrowsWriteRule(string pattern)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        AcceptedJob good = await JobAsync(ct);
        var job = new AcceptedJob(new BusFile(pattern.Replace("{id}", good.File.Id.Value, StringComparison.Ordinal), good.File.Id), good.Job);

        // Act
        Func<Task> act = () => Repository().ClaimAsync(job, ct);

        // Assert
        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("write rule:*");
        _processes.Calls.Should().BeEmpty();
        await _provider.DidNotReceive().PullAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("From https://example.invalid\nfatal: Cannot rebase onto multiple branches.")]
    [InlineData("error: cannot lock ref 'refs/remotes/origin/main': is at f934 but expected 6196")]
    [InlineData("warning: fetch updated the current branch head.")]
    public async Task PullAsync_ConcurrentChange_RetriedOnceAfterOneSecond(string stderr)
    {
        // Arrange: patch 0.8.1 — git refused because another process touched the checkout
        var ct = TestContext.Current.CancellationToken;
        _provider.PullAsync(Arg.Any<CancellationToken>()).Returns(new BusSyncResult(false, stderr), new BusSyncResult(true, string.Empty));

        // Act
        Task<BusSyncResult> task = Repository().PullAsync(ct);
        await Task.Delay(20, ct);
        bool waiting = !task.IsCompleted;
        _clock.Advance(TimeSpan.FromSeconds(1));
        BusSyncResult result = await task.WaitAsync(TimeSpan.FromSeconds(5), ct);

        // Assert
        waiting.Should().BeTrue();
        result.Succeeded.Should().BeTrue();
        await _provider.Received(2).PullAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PullAsync_OtherFailure_NotRetried()
    {
        // Arrange
        _provider.PullAsync(Arg.Any<CancellationToken>()).Returns(new BusSyncResult(false, "fatal: unable to access: Could not resolve host"));

        // Act
        BusSyncResult result = await Repository().PullAsync(TestContext.Current.CancellationToken);

        // Assert
        result.Succeeded.Should().BeFalse();
        await _provider.Received(1).PullAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ClaimAsync_PullHitsConcurrentChange_RetriedAndPushed()
    {
        // Arrange: the write sequence's own first pull also gets the one retry
        var ct = TestContext.Current.CancellationToken;
        _provider.PullAsync(Arg.Any<CancellationToken>()).Returns(new BusSyncResult(false, "fatal: Cannot rebase onto multiple branches."), new BusSyncResult(true, string.Empty));
        AcceptedJob job = await JobAsync(ct);

        // Act
        Task<WriteOutcome> task = Repository().ClaimAsync(job, ct);
        for (int i = 0; i < 50 && !task.IsCompleted; i++)
        {
            _clock.Advance(TimeSpan.FromMilliseconds(500));
            await Task.Delay(5, ct);
        }

        // Assert
        (await task).Should().Be(new Pushed(1));
    }

    [Fact]
    public async Task ClaimAsync_WriteLockHeldByAnotherProcess_WaitsThenWrites()
    {
        // Arrange: another writer holds the checkout lock; the claim waits for it instead of colliding
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Path.Combine(_checkout.Root, ".git"));
        AcceptedJob job = await JobAsync(ct);
        IDisposable? other = await BusCheckoutLock.AcquireAsync(_checkout.Root, BusCheckoutLock.WriteLockName, TimeSpan.Zero, _clock, ct);

        // Act
        Task<WriteOutcome> task = Repository().ClaimAsync(job, ct);
        _clock.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(50, ct);
        bool waited = !task.IsCompleted && _processes.Calls.Count == 0;
        other!.Dispose();
        for (int i = 0; i < 50 && !task.IsCompleted; i++)
        {
            _clock.Advance(TimeSpan.FromMilliseconds(500));
            await Task.Delay(5, ct);
        }

        // Assert
        waited.Should().BeTrue();
        (await task).Should().Be(new Pushed(1));
    }

    // ----- plan 07 Step 5: the §5 item 6 safety fetch -----

    [Fact]
    public async Task SafetyFetchAsync_RemoteEqualsHead_Unchanged()
    {
        // Act: the recorder answers every rev-parse with the same SHA
        SafetyFetchResult result = await Repository().SafetyFetchAsync(TestContext.Current.CancellationToken);

        // Assert
        result.Outcome.Should().Be(SafetyFetchOutcome.Unchanged);
    }

    [Fact]
    public async Task SafetyFetchAsync_RemoteDiffers_MovedWithShortSha()
    {
        // Arrange
        _processes.Hook = spec => spec.Arguments.Contains("origin/main") ? RecordingProcessRunner.Ok("abcdef0123456789abcdef0123456789abcdef01\n") : null;

        // Act
        SafetyFetchResult result = await Repository().SafetyFetchAsync(TestContext.Current.CancellationToken);

        // Assert
        result.Should().Be(new SafetyFetchResult(SafetyFetchOutcome.Moved, "abcdef012345"));
    }

    [Fact]
    public async Task SafetyFetchAsync_FetchFails_FailedRedactedDetail()
    {
        // Arrange: git echoes the URL with the credential and three lines of context
        _processes.On("fetch", RecordingProcessRunner.Fail(128, "fatal: unable to access 'https://x:" + TestKeys.TestPat + "@example.invalid/acme-org/acme-bus.git/'\nline two\nline three"));

        // Act
        SafetyFetchResult result = await Repository().SafetyFetchAsync(TestContext.Current.CancellationToken);

        // Assert
        result.Outcome.Should().Be(SafetyFetchOutcome.Failed);
        result.Detail.Split('\n').Should().HaveCountLessThanOrEqualTo(2);
        result.Detail.Should().Contain("<redacted>@").And.NotContain("line three");
        SecretSweep.AssertAbsent([result.Detail]);
        _processes.CallsOf("rev-parse").Should().BeEmpty("nothing is compared after a failed fetch");
    }

    [Fact]
    public async Task SafetyFetchAsync_RunsFetchThenTwoRevParse_UnderTheWriteLock()
    {
        // Arrange: a .git directory makes the checkout lock real; the hook watches the lock file while git runs
        Directory.CreateDirectory(Path.Combine(_checkout.Root, ".git"));
        string lockPath = Path.Combine(_checkout.Root, ".git", BusCheckoutLock.WriteLockName);
        var lockHeldDuring = new List<bool>();
        _processes.Hook = spec =>
        {
            lockHeldDuring.Add(File.Exists(lockPath));
            return null;
        };

        // Act
        await Repository().SafetyFetchAsync(TestContext.Current.CancellationToken);

        // Assert
        _processes.Calls.Select(RecordingProcessRunner.SubCommand).Should().Equal("fetch", "rev-parse", "rev-parse");
        _processes.Calls[1].Arguments.Should().EndWith(["rev-parse", "origin/main"]);
        _processes.Calls[2].Arguments.Should().EndWith(["rev-parse", "HEAD"]);
        lockHeldDuring.Should().Equal(true, true, true);
        File.Exists(lockPath).Should().BeFalse("the lock is released when the fetch is done");
    }

    [Fact]
    public async Task SecretSweep_OverEveryRecordedSpecAndLog_Clean()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        _provider.PushAsync(Arg.Any<CancellationToken>()).Returns(new Rejected("fetch first"));
        _processes.On("status", RecordingProcessRunner.Ok("?? stray.txt\0"));
        BusRepository repository = Repository();
        await repository.EnsureCleanAsync(ct);
        Task<WriteOutcome> task = repository.ClaimAsync(await JobAsync(ct), ct);
        while (!task.IsCompleted)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(1, ct);
        }

        await task;

        // Act & Assert
        SecretSweep.AssertAbsent(_processes.Calls.SelectMany(SecretSweep.TextsOf));
        SecretSweep.AssertAbsent(_log.Messages);
    }

    private BusRepository Repository(string? root = null)
    {
        var git = new GitClient(_processes, new GitClientOptions { Identity = new GitIdentity("zyggy (home-laptop)", "home-laptop@zyggy.org") }, _clock);
        return new BusRepository(git, _provider, Me, root ?? _checkout.Root, new EnvelopeSigner(new Zyggy.Core.Secrets.InMemorySecretStore()), _clock, () => _jitter, _log);
    }

    private async Task<AcceptedJob> JobAsync(CancellationToken ct)
    {
        var store = await TestKeys.StoreWithBothAsync(ct);
        Zyggy.Core.Envelope.Envelope job = await TestEnvelopes.SignedJobAsync(Central, HomeLaptop, store, TestKeys.Acme1, "probe\n", ct);
        string path = BusPaths.EnvelopeFile(BusPaths.Jobs(Acme, HomeLaptop), job.Header.Id);
        _checkout.Drop(path, EnvelopeWriter.Serialize(job));
        return new AcceptedJob(new BusFile(path, job.Header.Id), job);
    }

    // Spec 12 Step 2 (D1): a context envelope is written into the sender's own context/ folder, addressed to central.
    [Fact]
    public async Task PublishContextAsync_SignedContextFromMe_FileUnderMyContextFolderAddCommitPushWithSubject()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Zyggy.Core.Envelope.Envelope context = await SignedContextAsync(HomeLaptop, ct);
        string path = BusPaths.EnvelopeFile(BusPaths.Context(Acme, HomeLaptop), context.Header.Id);

        // Act
        WriteOutcome outcome = await Repository().PublishContextAsync(context, ct);

        // Assert
        outcome.Should().Be(new Pushed(1));
        _checkout.Read(path).Should().Equal(EnvelopeWriter.Serialize(context));
        _processes.CallsOf("add").Should().ContainSingle().Which.Arguments.Should().Contain(path);
        _processes.CallsOf("commit").Should().ContainSingle().Which.StandardInput.Should().StartWith($"acme context {context.Header.Id.Value} home-laptop→central");
    }

    [Fact]
    public async Task PublishContextAsync_Unsigned_ThrowsInvalidOperation()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Zyggy.Core.Envelope.Envelope unsigned = Zyggy.Core.Envelope.Envelope.CreateContext(
            new EnvelopeHeader(Acme, EnvelopeId.New(T), HomeLaptop, Central, T), new ContextFields(ContextScope.Parse("general")), "- fact\n"u8.ToArray());

        // Act
        Func<Task> act = () => Repository().PublishContextAsync(unsigned, ct);

        // Assert
        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("write rule:*");
        _processes.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task PublishContextAsync_ForeignTenant_Throws()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Zyggy.Core.Envelope.Envelope context = await SignedContextAsync(HomeLaptop, ct, tenant: TenantId.Parse("globex"), signWith: KeyId.Parse("globex/1"));

        // Act
        Func<Task> act = () => Repository().PublishContextAsync(context, ct);

        // Assert
        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("write rule:*");
        _processes.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task PublishContextAsync_FromAnotherMachine_Throws()
    {
        // Arrange: a context signed as central, published by home-laptop's repository
        var ct = TestContext.Current.CancellationToken;
        Zyggy.Core.Envelope.Envelope context = await SignedContextAsync(Central, ct);

        // Act
        Func<Task> act = () => Repository().PublishContextAsync(context, ct);

        // Assert
        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("write rule:*");
        _processes.Calls.Should().BeEmpty();
        _checkout.FilesUnder(BusPaths.Context(Acme, Central)).Should().BeEmpty();
    }

    [Fact]
    public async Task PublishContextAsync_NotAContextEnvelope_Throws()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var store = await TestKeys.StoreWithBothAsync(ct);
        Zyggy.Core.Envelope.Envelope job = await TestEnvelopes.SignedJobAsync(HomeLaptop, Central, store, TestKeys.Acme1, "probe\n", ct);

        // Act
        Func<Task> act = () => Repository().PublishContextAsync(job, ct);

        // Assert
        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("write rule:*");
        _processes.Calls.Should().BeEmpty();
    }

    private static async Task<Zyggy.Core.Envelope.Envelope> SignedContextAsync(MachineName from, CancellationToken ct, TenantId? tenant = null, KeyId? signWith = null)
    {
        var store = signWith is null ? await TestKeys.StoreWithBothAsync(ct) : await TestKeys.StoreWithAsync(signWith, ct);
        var header = new EnvelopeHeader(tenant ?? Acme, EnvelopeId.New(T), from, Central, T);
        Zyggy.Core.Envelope.Envelope context = Zyggy.Core.Envelope.Envelope.CreateContext(header, new ContextFields(ContextScope.Parse("project:calizr")), "- [stated] 2026-09-30 (project:calizr): fact\n"u8.ToArray());
        EnvelopeResult signed = await new EnvelopeSigner(store).SignAsync(context, signWith ?? TestKeys.Acme1, ct);
        signed.IsAccepted.Should().BeTrue(signed.Rejection?.ToString());
        return signed.Envelope!;
    }
}
