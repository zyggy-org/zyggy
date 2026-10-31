using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Git;
using Zyggy.Core.Memory;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

using ContextRequest = Zyggy.Core.Bus.ContextRequest;
using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Bus;

/// <summary>Spec 12 AC-7, AC-9..AC-11 through the seams: `home-laptop` publishes a stated fact into its own context/ folder, and every failure is a named outcome with nothing written.</summary>
public sealed class ContextSubmitterSubmitTests : IAsyncLifetime
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly DateTimeOffset T = new(2026, 10, 28, 10, 42, 7, TimeSpan.Zero);

    private readonly TempCheckout _checkout = new(Acme, HomeLaptop);

    private readonly RecordingProcessRunner _processes = new();

    private readonly IBusProvider _provider = Substitute.For<IBusProvider>();

    private readonly RecordingLogger<ContextSubmitter> _log = new();

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
    public async Task SubmitAsync_PushSucceeds_SubmittedWithContextPathAndTheFileVerifies()
    {
        // Act
        var ct = TestContext.Current.CancellationToken;
        SubmitResult result = await Submitter().SubmitAsync(Request(), ct);

        // Assert
        var submitted = result.Should().BeOfType<Submitted>().Subject;
        string expectedPath = BusPaths.EnvelopeFile(BusPaths.Context(Acme, HomeLaptop), submitted.Id);
        submitted.RelativePath.Should().Be(expectedPath).And.StartWith("tenants/acme/nodes/home-laptop/context/");
        submitted.PushAttempts.Should().Be(1);
        EnvelopeResult check = await new EnvelopeSigner(_store).ParseAndVerifyAsync(_checkout.Read(expectedPath), Acme, ct);
        check.IsAccepted.Should().BeTrue(check.Rejection?.ToString());
        check.Envelope!.Header.To.Should().Be(MachineName.Central);
        check.Envelope.Header.From.Should().Be(HomeLaptop);
        _processes.CallsOf("commit").Should().ContainSingle().Which.StandardInput.Should().StartWith($"acme context {submitted.Id.Value} home-laptop→central");
        _log.AtLevel(LogLevel.Information).Select(e => e.Message).Should().ContainSingle().Which.Should().Be($"published context {submitted.Id.Value} to central");
    }

    [Fact]
    public async Task SubmitAsync_PushRejectedFourTimes_SubmitDeferredAttemptsFour_FileStays()
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
        deferred.Detail.Should().Be("fetch first");
        _checkout.Exists(deferred.RelativePath).Should().BeTrue("the commit stays local");
        _log.AtLevel(LogLevel.Warning).Select(e => e.Message).Should().ContainSingle().Which.Should().StartWith($"published context {deferred.Id.Value} to central: committed locally, not pushed");
    }

    [Fact]
    public async Task SubmitAsync_PullFails_SubmitFailedPull_NoFileNoAdd()
    {
        // Arrange
        _provider.PullAsync(Arg.Any<CancellationToken>()).Returns(new BusSyncResult(false, "fatal: unable to access"));

        // Act
        SubmitResult result = await Submitter().SubmitAsync(Request(), TestContext.Current.CancellationToken);

        // Assert
        var failed = result.Should().BeOfType<SubmitFailed>().Subject;
        failed.Stage.Should().Be("pull");
        _checkout.FilesUnder(BusPaths.Context(Acme, HomeLaptop)).Should().BeEmpty();
        _processes.CallsOf("add").Should().BeEmpty();
    }

    [Fact]
    public async Task SubmitAsync_AddFails_SubmitFailedAdd_CheckoutRestored()
    {
        // Arrange
        _processes.On("add", RecordingProcessRunner.Fail(128, "fatal: pathspec"));
        _processes.On("checkout", RecordingProcessRunner.Fail(1, "error: pathspec did not match any file(s) known to git"));

        // Act
        SubmitResult result = await Submitter().SubmitAsync(Request(), TestContext.Current.CancellationToken);

        // Assert
        result.Should().BeOfType<SubmitFailed>().Which.Stage.Should().Be("add");
        _checkout.FilesUnder(BusPaths.Context(Acme, HomeLaptop)).Should().BeEmpty("the checkout is restored");
        _processes.CallsOf("commit").Should().BeEmpty();
    }

    [Fact]
    public async Task SubmitAsync_KeyAbsent_SubmitFailedSign_NothingWritten()
    {
        // Act
        SubmitResult result = await Submitter(signWith: KeyId.Parse("acme/9")).SubmitAsync(Request(), TestContext.Current.CancellationToken);

        // Assert
        result.Should().BeOfType<SubmitFailed>().Which.Stage.Should().Be("sign");
        _processes.Calls.Should().BeEmpty();
        _checkout.FilesUnder(BusPaths.Context(Acme, HomeLaptop)).Should().BeEmpty();
    }

    [Fact]
    public async Task SubmitAsync_StoreUnavailable_SubmitFailedSecretStore()
    {
        // Act
        SubmitResult result = await Submitter(store: new ThrowingSecretStore()).SubmitAsync(Request(), TestContext.Current.CancellationToken);

        // Assert
        result.Should().BeOfType<SubmitFailed>().Which.Stage.Should().Be("secret-store");
        _processes.Calls.Should().BeEmpty();
        _checkout.FilesUnder(BusPaths.Context(Acme, HomeLaptop)).Should().BeEmpty();
    }

    [Fact]
    public async Task SubmitAsync_SecretShapedFact_ContextRefusedNamesPattern_BusNeverCalled_NothingWritten()
    {
        // Arrange
        const string fact = "the key is AKIAIOSFODNN7EXAMPLE";

        // Act
        SubmitResult result = await Submitter().SubmitAsync(Request(fact), TestContext.Current.CancellationToken);

        // Assert
        result.Should().Be(new ContextRefused("aws-access-key"));
        _processes.Calls.Should().BeEmpty();
        _provider.ReceivedCalls().Should().BeEmpty();
        _checkout.FilesUnder(BusPaths.Context(Acme, HomeLaptop)).Should().BeEmpty();
        _log.Messages.Should().NotContain(m => m.Contains("AKIAIOSFODNN7EXAMPLE", StringComparison.Ordinal));
        SecretSweep.AssertAbsent(_log.Messages);
    }

    [Fact]
    public async Task SubmitAsync_SelfCheckRefuses_SubmitRefused_BusNeverCalled()
    {
        // Arrange
        ContextSubmitter submitter = Submitter(serializer: e => [.. EnvelopeWriter.Serialize(e), (byte)'x']);

        // Act
        SubmitResult result = await submitter.SubmitAsync(Request(), TestContext.Current.CancellationToken);

        // Assert
        result.Should().BeOfType<SubmitRefused>();
        _processes.Calls.Should().BeEmpty();
        _provider.ReceivedCalls().Should().BeEmpty();
        _log.AtLevel(LogLevel.Warning).Select(e => e.Message).Should().ContainSingle().Which.Should().StartWith("refusing context: the envelope does not round-trip (");
    }

    [Fact]
    public async Task SubmitAsync_LockHeldByAnother_SubmitFailedLock()
    {
        // Arrange
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
    public async Task SubmitAsync_NeverLogsFactOrKey()
    {
        // Arrange
        const string token = "fact-token-9c41-do-not-log";

        // Act
        await Submitter().SubmitAsync(Request("Remember " + token), TestContext.Current.CancellationToken);

        // Assert
        _log.Messages.Should().NotContain(m => m.Contains(token, StringComparison.Ordinal));
        SecretSweep.AssertAbsent(_log.Messages);
    }

    private static ContextRequest Request(string fact = "The staging database is restored from Sunday's snapshot")
    {
        ContextRequest.TryCreateStated("project:calizr", fact, new DateOnly(2026, 10, 28), out var request).Should().BeNull();
        return request!;
    }

    private ContextSubmitter Submitter(ISecretStore? store = null, Func<Model, byte[]>? serializer = null, KeyId? signWith = null)
    {
        var git = new GitClient(_processes, new GitClientOptions { Identity = TestGitIdentities.HomeLaptop }, _clock);
        var signer = new EnvelopeSigner(store ?? _store);
        var bus = new BusRepository(git, _provider, new BusIdentity(Acme, HomeLaptop), _checkout.Root, signer, _clock, () => 0, new RecordingLogger<BusRepository>());
        var patterns = SecretPatterns.Load(Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt")).Patterns!;
        var submitter = new ContextSubmitter(bus, signer, signWith ?? TestKeys.Acme1, patterns, new ContextEnvelopeOptions(), _clock, _log);
        return serializer is null ? submitter : new ContextSubmitter(bus, signer, signWith ?? TestKeys.Acme1, patterns, new ContextEnvelopeOptions(), _clock, _log) { Serializer = serializer };
    }
}
