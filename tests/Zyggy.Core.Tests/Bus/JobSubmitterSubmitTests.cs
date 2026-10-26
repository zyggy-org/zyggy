using System.Text;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Git;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Bus;

/// <summary>Plan 08 Step 2: a built job reaches the sender's checkout through the existing §4 write rule and every outcome is named; nothing is written when the self-check or the key refuses.</summary>
public sealed class JobSubmitterSubmitTests : IAsyncLifetime
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName Central = MachineName.Parse("central");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly DateTimeOffset T = new(2026, 10, 24, 10, 0, 0, TimeSpan.Zero);

    private readonly TempCheckout _checkout = new(Acme, Central);

    private readonly RecordingProcessRunner _processes = new();

    private readonly IBusProvider _provider = Substitute.For<IBusProvider>();

    private readonly RecordingLogger<JobSubmitter> _log = new();

    private readonly FakeTimeProvider _clock = new(T);

    private InMemorySecretStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = await TestKeys.StoreWithBothAsync(CancellationToken.None);
        _provider.PullAsync(Arg.Any<CancellationToken>()).Returns(new BusSyncResult(true, string.Empty));
        _provider.PushAsync(Arg.Any<CancellationToken>()).Returns(new PushSucceeded());
    }

    public ValueTask DisposeAsync()
    {
        _checkout.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task SubmitAsync_PushSucceeds_SubmittedWithPathAndTheFileInJobsOfTheTarget()
    {
        // Act
        var ct = TestContext.Current.CancellationToken;
        SubmitResult result = await Submitter().SubmitAsync(Request(), ct);

        // Assert
        var submitted = result.Should().BeOfType<Submitted>().Subject;
        string expectedPath = BusPaths.EnvelopeFile(BusPaths.Jobs(Acme, HomeLaptop), submitted.Id);
        submitted.RelativePath.Should().Be(expectedPath);
        submitted.PushAttempts.Should().Be(1);
        _checkout.Exists(expectedPath).Should().BeTrue();
        EnvelopeResult check = await new EnvelopeSigner(_store).ParseAndVerifyAsync(await File.ReadAllBytesAsync(BusPaths.Resolve(_checkout.Root, expectedPath), ct), Acme, ct);
        check.IsAccepted.Should().BeTrue(check.Rejection?.ToString());
        check.Envelope!.Header.To.Should().Be(HomeLaptop);
        _processes.CallsOf("add").Should().ContainSingle().Which.Arguments.Should().Contain(expectedPath);
        _processes.CallsOf("commit").Should().ContainSingle().Which.StandardInput.Should().StartWith($"acme job {submitted.Id.Value} central→home-laptop");
        _log.AtLevel(LogLevel.Information).Select(e => e.Message).Should().ContainSingle().Which.Should().Be($"submitted job {submitted.Id.Value} to home-laptop");
    }

    [Fact]
    public async Task SubmitAsync_SelfAddressed_SubmittedIntoOwnJobs()
    {
        // Act: ex-Q10 — Central's cron-style jobs to itself
        SubmitResult result = await Submitter().SubmitAsync(Request(to: "central"), TestContext.Current.CancellationToken);

        // Assert
        var submitted = result.Should().BeOfType<Submitted>().Subject;
        submitted.RelativePath.Should().Be(BusPaths.EnvelopeFile(BusPaths.Jobs(Acme, Central), submitted.Id));
        _checkout.Exists(submitted.RelativePath).Should().BeTrue();
    }

    [Fact]
    public async Task SubmitAsync_PushRejectedFourTimes_SubmitDeferredWithAttemptsFour()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        _provider.PushAsync(Arg.Any<CancellationToken>()).Returns(new Rejected("fetch first"));

        // Act
        Task<SubmitResult> task = Submitter().SubmitAsync(Request(), ct);
        while (!task.IsCompleted)
        {
            _clock.Advance(TimeSpan.FromMilliseconds(500));
            await Task.Delay(1, ct);
        }

        SubmitResult result = await task;

        // Assert
        var deferred = result.Should().BeOfType<SubmitDeferred>().Subject;
        deferred.PushAttempts.Should().Be(4);
        deferred.Detail.Should().Be("fetch first", "the provider's redacted detail is passed through");
        _checkout.Exists(deferred.RelativePath).Should().BeTrue("the commit stays local");
        _log.AtLevel(LogLevel.Warning).Select(e => e.Message).Should().ContainSingle().Which.Should().StartWith($"submitted job {deferred.Id.Value} to home-laptop: committed locally, not pushed");
    }

    [Fact]
    public async Task SubmitAsync_PullFails_SubmitFailedPullAndNoFile()
    {
        // Arrange
        _provider.PullAsync(Arg.Any<CancellationToken>()).Returns(new BusSyncResult(false, "fatal: unable to access"));

        // Act
        SubmitResult result = await Submitter().SubmitAsync(Request(), TestContext.Current.CancellationToken);

        // Assert
        var failed = result.Should().BeOfType<SubmitFailed>().Subject;
        failed.Stage.Should().Be("pull");
        _processes.CallsOf("add").Should().BeEmpty();
        _checkout.FilesUnder(BusPaths.Jobs(Acme, HomeLaptop)).Should().BeEmpty();
    }

    [Fact]
    public async Task SubmitAsync_AddFails_SubmitFailedAddAndFileRemoved()
    {
        // Arrange: `add` fails; the restore's `checkout` of a path unknown to HEAD fails as the real git does, so the new file is deleted
        _processes.On("add", RecordingProcessRunner.Fail(128, "fatal: pathspec"));
        _processes.On("checkout", RecordingProcessRunner.Fail(1, "error: pathspec did not match any file(s) known to git"));

        // Act
        SubmitResult result = await Submitter().SubmitAsync(Request(), TestContext.Current.CancellationToken);

        // Assert
        var failed = result.Should().BeOfType<SubmitFailed>().Subject;
        failed.Stage.Should().Be("add");
        _checkout.FilesUnder(BusPaths.Jobs(Acme, HomeLaptop)).Should().BeEmpty("the checkout is restored");
        _processes.CallsOf("commit").Should().BeEmpty();
    }

    [Fact]
    public async Task SubmitAsync_KeyAbsent_SubmitFailedSignAndNothingWritten()
    {
        // Arrange: a store without acme/1
        var empty = new InMemorySecretStore();

        // Act
        SubmitResult result = await Submitter(store: empty).SubmitAsync(Request(), TestContext.Current.CancellationToken);

        // Assert
        var failed = result.Should().BeOfType<SubmitFailed>().Subject;
        failed.Stage.Should().Be("sign");
        _processes.Calls.Should().BeEmpty();
        _checkout.FilesUnder(BusPaths.Jobs(Acme, HomeLaptop)).Should().BeEmpty();
    }

    [Fact]
    public async Task SubmitAsync_KeyShorterThan32Bytes_SubmitFailedSign()
    {
        // Arrange
        var store = new InMemorySecretStore();
        await store.SetAsync(Acme, TestKeys.Acme1.SecretName, Encoding.UTF8.GetBytes("short"), TestContext.Current.CancellationToken);

        // Act
        SubmitResult result = await Submitter(store: store).SubmitAsync(Request(), TestContext.Current.CancellationToken);

        // Assert
        var failed = result.Should().BeOfType<SubmitFailed>().Subject;
        failed.Stage.Should().Be("sign");
        failed.Detail.Should().Contain("32 bytes");
        SecretSweep.AssertAbsent([failed.Detail]);
    }

    [Fact]
    public async Task SubmitAsync_SelfCheckRefuses_SubmitRefusedAndBusNeverCalled()
    {
        // Arrange: plan A1 — a serializer that alters a field
        JobSubmitter submitter = Submitter(serializer: e => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(EnvelopeWriter.Serialize(e)).Replace("project: calizr", "project: calizr2", StringComparison.Ordinal)));

        // Act
        SubmitResult result = await submitter.SubmitAsync(Request(), TestContext.Current.CancellationToken);

        // Assert
        var refused = result.Should().BeOfType<SubmitRefused>().Subject;
        refused.Field.Should().BeOneOf("project", "sig");
        _processes.Calls.Should().BeEmpty();
        _provider.ReceivedCalls().Should().BeEmpty();
        _log.AtLevel(LogLevel.Warning).Select(e => e.Message).Should().ContainSingle().Which.Should().Be($"refusing to submit: the envelope does not round-trip ({refused.Field})");
    }

    [Fact]
    public async Task SubmitAsync_LockHeldByAnother_SubmitFailedLock()
    {
        // Arrange: another writer holds the checkout lock for longer than the write timeout
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Path.Combine(_checkout.Root, ".git"));
        using IDisposable? other = await BusCheckoutLock.AcquireAsync(_checkout.Root, BusCheckoutLock.WriteLockName, TimeSpan.Zero, _clock, ct);

        // Act
        Task<SubmitResult> task = Submitter().SubmitAsync(Request(), ct);
        for (int i = 0; i < 200 && !task.IsCompleted; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(5, ct);
        }

        SubmitResult result = await task;

        // Assert
        var failed = result.Should().BeOfType<SubmitFailed>().Subject;
        failed.Stage.Should().Be("lock");
        failed.Detail.Should().Contain("busy");
        _processes.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task SubmitAsync_NeverLogsBodyOrKey()
    {
        // Arrange
        const string token = "body-token-3b7e-do-not-log";

        // Act
        await Submitter().SubmitAsync(Request(body: "Investigate " + token + "\n"), TestContext.Current.CancellationToken);

        // Assert
        _log.Messages.Should().NotContain(m => m.Contains(token, StringComparison.Ordinal));
        SecretSweep.AssertAbsent(_log.Messages);
        SecretSweep.AssertAbsent(_processes.Calls.SelectMany(SecretSweep.TextsOf));
    }

    [Fact]
    public async Task SubmitAsync_StoreUnavailable_SubmitFailedSecretStoreAndNothingWritten()
    {
        // Act: spec 09 AC-2 — the sender's store cannot be opened; the "never throws" contract holds
        SubmitResult result = await Submitter(store: new ThrowingSecretStore()).SubmitAsync(Request(), TestContext.Current.CancellationToken);

        // Assert
        var failed = result.Should().BeOfType<SubmitFailed>().Subject;
        failed.Stage.Should().Be("secret-store");
        failed.Detail.Should().Be("credential-manager: 1312 No such logon session");
        _processes.CallsOf("add").Should().BeEmpty();
        _checkout.FilesUnder(BusPaths.Jobs(Acme, HomeLaptop)).Should().BeEmpty();
        _log.AtLevel(LogLevel.Warning).Should().ContainSingle();
    }

    private JobSubmitter Submitter(ISecretStore? store = null, Func<Model, byte[]>? serializer = null)
    {
        var git = new GitClient(_processes, new GitClientOptions { Identity = new GitIdentity("zyggy (central)", "central@zyggy.org") }, _clock);
        var signer = new EnvelopeSigner(store ?? _store);
        var bus = new BusRepository(git, _provider, new BusIdentity(Acme, Central), _checkout.Root, signer, _clock, () => 0, new RecordingLogger<BusRepository>());
        return serializer is null
            ? new JobSubmitter(bus, signer, TestKeys.Acme1, _clock, _log)
            : new JobSubmitter(bus, signer, TestKeys.Acme1, _clock, _log) { Serializer = serializer };
    }

    private static JobRequest Request(string to = "home-laptop", string body = "Investigate 502.\n")
    {
        JobRequest.TryCreate(to, "calizr", null, null, null, null, body, out JobRequest? request).Should().BeNull();
        return request!;
    }
}
