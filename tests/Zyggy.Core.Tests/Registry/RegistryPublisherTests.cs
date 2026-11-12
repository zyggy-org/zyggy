using System.Text;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Git;
using Zyggy.Core.Health;
using Zyggy.Core.Registry;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Registry;

/// <summary>Spec 14 AC-15, AC-30 (publisher half): commit on changed content or a stale liveness stamp, log once per state change.</summary>
public sealed class RegistryPublisherTests : IDisposable
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly BusRemote Remote = new("acme-org", "acme-bus", "main", new Uri("https://example.invalid/acme-org/acme-bus.git"), null);

    private static readonly DateTimeOffset T = new(2026, 11, 7, 9, 0, 0, TimeSpan.Zero);

    private readonly TempCheckout _checkout = new(Acme, HomeLaptop);
    private readonly RecordingProcessRunner _processes = new();
    private readonly IBusProvider _provider = Substitute.For<IBusProvider>();
    private readonly FakeTimeProvider _clock = new(T);
    private readonly SettableClock _now = new(T);
    private readonly RecordingLogger<RegistryPublisher> _log = new();
    private readonly RegistryPublisher _publisher;

    public RegistryPublisherTests()
    {
        _provider.Remote.Returns(Remote);
        _provider.PullAsync(Arg.Any<CancellationToken>()).Returns(new BusSyncResult(true, string.Empty));
        _provider.PushAsync(Arg.Any<CancellationToken>()).Returns(new PushSucceeded());
        var bus = new BusRepository(new GitClient(_processes, new GitClientOptions(), _clock), _provider, new BusIdentity(Acme, HomeLaptop), _checkout.Root,
            new EnvelopeSigner(new Zyggy.Core.Secrets.InMemorySecretStore()), _clock, () => 0, new RecordingLogger<BusRepository>());
        _publisher = new RegistryPublisher(bus, Options.Create(new DiscoveryOptions()), _now, _log);
    }

    public void Dispose() => _checkout.Dispose();

    private static string RegistryPath => BusPaths.Registry(Acme, HomeLaptop);

    private static RegistryDocument Content() => RegistryDocumentTests.HomeLaptop() with { LastSeen = null, Health = null };

    private RegistryDocument OnBus() => RegistryDocument.Parse(_checkout.Read(RegistryPath)).Document!;

    private int Commits => _processes.CallsOf("commit").Count();

    /// <summary>Runs a publish to completion while advancing the fake clock (the push retry waits on it).</summary>
    private async Task<RegistryPublishResult> PublishAsync(RegistryDocument content, HealthLevel? health)
    {
        var ct = TestContext.Current.CancellationToken;
        Task<RegistryPublishResult> task = _publisher.PublishAsync(content, health, ct);
        for (int i = 0; i < 400 && !task.IsCompleted; i++)
        {
            _clock.Advance(TimeSpan.FromMilliseconds(500));
            await Task.Delay(1, ct);
        }

        RegistryPublishResult result = await task;
        return result;
    }

    [Fact]
    public async Task PublishAsync_LivenessSequence()
    {
        // First publish
        (await PublishAsync(Content(), HealthLevel.Green)).Outcome.Should().Be(PublishOutcome.Published);
        OnBus().LastSeen.Should().Be(T);
        OnBus().Health.Should().Be(HealthLevel.Green);
        byte[] first = _checkout.Read(RegistryPath);

        // Same content one hour later: unchanged, no commit
        _now.Now = T.AddHours(1);
        (await PublishAsync(Content(), HealthLevel.Green)).Outcome.Should().Be(PublishOutcome.Unchanged);
        Commits.Should().Be(1);

        // Six hours and a minute later: refreshed
        _now.Now = T.AddHours(6).AddMinutes(1);
        (await PublishAsync(Content(), HealthLevel.Green)).Outcome.Should().Be(PublishOutcome.Published);
        Commits.Should().Be(2);
        OnBus().LastSeen.Should().Be(T.AddHours(6).AddMinutes(1));
        static string Strip(byte[] bytes) => string.Join('\n', Encoding.UTF8.GetString(bytes).Split('\n').Where(l => !l.StartsWith("last_seen:", StringComparison.Ordinal)));
        Strip(_checkout.Read(RegistryPath)).Should().Be(Strip(first));

        // A changed description: published with both liveness keys refreshed
        _now.Now = T.AddHours(8).AddMinutes(1);
        RegistryDocument changed = Content() with
        {
            Projects = [Content().Projects[0] with { Skills = [new RegistryEntry("deploy-staging", "Deploys to staging")] }],
        };
        (await PublishAsync(changed, HealthLevel.Amber)).Outcome.Should().Be(PublishOutcome.Published);
        OnBus().LastSeen.Should().Be(T.AddHours(8).AddMinutes(1));
        OnBus().Health.Should().Be(HealthLevel.Amber);

        // A clock behind the file's stamp: the future stamp counts as fresh
        _now.Now = T.AddHours(7);
        (await PublishAsync(changed, HealthLevel.Amber)).Outcome.Should().Be(PublishOutcome.Unchanged);
        Commits.Should().Be(3);
    }

    [Fact]
    public async Task PublishAsync_HealthNull_KeepsBusCopyValue()
    {
        // Arrange
        await PublishAsync(Content(), HealthLevel.Red);
        _now.Now = T.AddHours(7);

        // Act
        await PublishAsync(Content(), null);

        // Assert
        OnBus().Health.Should().Be(HealthLevel.Red);
        OnBus().LastSeen.Should().Be(T.AddHours(7));
    }

    [Fact]
    public async Task PublishAsync_HealthNullNoCopy_KeyOmitted()
    {
        // Act
        await PublishAsync(Content(), null);

        // Assert
        Encoding.UTF8.GetString(_checkout.Read(RegistryPath)).Should().NotContain("health:");
    }

    [Fact]
    public async Task PublishAsync_UnparseableBusCopy_TreatedAsAbsentWarnedOnce()
    {
        // Arrange
        byte[] garbage = Encoding.UTF8.GetBytes("this: [is not\n");

        // Act
        _checkout.Drop(RegistryPath, garbage);
        RegistryPublishResult first = await PublishAsync(Content(), HealthLevel.Green);
        _checkout.Drop(RegistryPath, garbage);
        RegistryPublishResult second = await PublishAsync(Content(), HealthLevel.Green);

        // Assert
        first.Outcome.Should().Be(PublishOutcome.Published);
        second.Outcome.Should().Be(PublishOutcome.Published);
        _log.AtLevel(LogLevel.Warning).Should().ContainSingle().Which.Message.Should().StartWith("registry: bus copy unreadable (");
    }

    [Fact]
    public async Task PublishAsync_PushRejectedOnceThenOk_PublishedOneRetryWarning()
    {
        // Arrange
        _provider.PushAsync(Arg.Any<CancellationToken>()).Returns(new Rejected("fetch first"), new PushSucceeded(), new Rejected("fetch first"), new PushSucceeded());
        RegistryDocument other = Content() with { Projects = [] };

        // Act
        RegistryPublishResult first = await PublishAsync(Content(), HealthLevel.Green);
        RegistryPublishResult second = await PublishAsync(other, HealthLevel.Green);

        // Assert
        first.Outcome.Should().Be(PublishOutcome.Published);
        second.Outcome.Should().Be(PublishOutcome.Published);
        _log.AtLevel(LogLevel.Warning).Select(e => e.Message).Should().Equal("registry: push retried");
    }

    [Fact]
    public async Task PublishAsync_RetryStreaks_WarnOncePerStreak()
    {
        // Arrange: retried, clean, retried again
        _provider.PushAsync(Arg.Any<CancellationToken>()).Returns(
            new Rejected("fetch first"), new PushSucceeded(), new PushSucceeded(), new Rejected("fetch first"), new PushSucceeded());

        // Act
        await PublishAsync(Content(), HealthLevel.Green);
        await PublishAsync(Content() with { Projects = [] }, HealthLevel.Green);
        await PublishAsync(Content(), HealthLevel.Green);

        // Assert
        _log.AtLevel(LogLevel.Warning).Select(e => e.Message).Should().Equal("registry: push retried", "registry: push retried");
    }

    [Fact]
    public async Task PublishAsync_EveryPushRejected_FailedWithRedactedDetail()
    {
        // Arrange
        _provider.PushAsync(Arg.Any<CancellationToken>()).Returns(new Rejected("! [rejected] https://user:tok@host.invalid/acme-bus.git"));

        // Act
        RegistryPublishResult first = await PublishAsync(Content(), HealthLevel.Green);
        RegistryPublishResult second = await PublishAsync(Content() with { Projects = [] }, HealthLevel.Green);
        _provider.PushAsync(Arg.Any<CancellationToken>()).Returns(new PushSucceeded());
        RegistryPublishResult third = await PublishAsync(Content(), HealthLevel.Green);

        // Assert
        first.Outcome.Should().Be(PublishOutcome.Failed);
        first.Detail.Should().Contain("://<redacted>@").And.NotContain("tok");
        second.Outcome.Should().Be(PublishOutcome.Failed);
        third.Outcome.Should().Be(PublishOutcome.Published);
        _log.AtLevel(LogLevel.Warning).Should().ContainSingle().Which.Message.Should().StartWith("registry: push failed: ");
        _log.Messages.Should().Contain("registry: publishing again");
        _log.Messages.Should().NotContain(m => m.Contains("tok@", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PublishAsync_LockHeld_Busy()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Path.Combine(_checkout.Root, ".git"));
        using IDisposable? other = await BusCheckoutLock.AcquireAsync(_checkout.Root, BusCheckoutLock.WriteLockName, TimeSpan.Zero, _clock, ct);

        // Act
        Task<RegistryPublishResult> task = _publisher.PublishAsync(Content(), HealthLevel.Green, ct);
        for (int i = 0; i < 200 && !task.IsCompleted; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(5, ct);
        }

        RegistryPublishResult result = await task;

        // Assert
        result.Outcome.Should().Be(PublishOutcome.Busy);
        _log.AtLevel(LogLevel.Warning).Should().ContainSingle().Which.Message.Should().Be("registry: checkout busy");
    }

    [Fact]
    public async Task PublishAsync_Logs_OneLinePerStateChange_NoContent()
    {
        // Act
        await PublishAsync(Content(), HealthLevel.Green);
        _now.Now = T.AddHours(1);
        await PublishAsync(Content(), HealthLevel.Green);

        // Assert
        _log.AtLevel(LogLevel.Information).Select(e => e.Message).Should().Equal("registry published: 1 project(s)");
        _log.AtLevel(LogLevel.Debug).Select(e => e.Message).Should().Equal("registry unchanged");
        _log.Messages.Should().NotContain(m => m.Contains("Diagnoses", StringComparison.Ordinal) || m.Contains("Blazor", StringComparison.Ordinal));
    }

    /// <summary>A wall clock the test sets freely, backwards included (the "clock behind the stamp" case); <see cref="FakeTimeProvider"/> cannot go back.</summary>
    private sealed class SettableClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
