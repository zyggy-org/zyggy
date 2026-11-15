using System.Text;

using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;
using Zyggy.Core.Envelope;
using Zyggy.Core.Git;
using Zyggy.Core.Memory;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Core.Tests.Memory;

namespace Zyggy.Core.Tests.Bus;

// AC-3: a status carries the fixed fields only on a machine with `detail: fixed`; a secret-shaped value or an unattended run writes nothing.
public sealed class WorkStatusPublisherTests : IAsyncLifetime
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly DateTimeOffset T = new(2026, 11, 15, 10, 42, 5, TimeSpan.Zero);

    private static readonly DateTimeOffset Since = new(2026, 11, 15, 9, 3, 0, TimeSpan.Zero);

    private static readonly WorkStatusRequest Full = new("calizr", "5b3fd3ac", Since, T, "feature/12-booking-pool", "9f2c1ab", 3, "_specs/12-booking-pool.md", "slice 2");

    private readonly TempCheckout _checkout = new(Acme, HomeLaptop);

    private readonly RecordingProcessRunner _processes = new();

    private readonly IBusProvider _provider = Substitute.For<IBusProvider>();

    private readonly RecordingLogger<WorkStatusPublisher> _log = new();

    private readonly FakeTimeProvider _clock = new(T);

    private InMemorySecretStore _store = null!;

    public static TheoryData<string, string> SecretSamples() => SecretPatternsTests.SecretSamples();

    public async ValueTask InitializeAsync()
    {
        _provider.PullAsync(Arg.Any<CancellationToken>()).Returns(new BusSyncResult(true, string.Empty));
        _provider.PushAsync(Arg.Any<CancellationToken>()).Returns(new PushSucceeded());
        _store = await TestKeys.StoreWithBothAsync(CancellationToken.None);
    }

    public ValueTask DisposeAsync()
    {
        _checkout.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task PublishAsync_DetailFull_SignedStatusToCentralWithEveryField_OnTheBus()
    {
        // Act
        WorkStatusResult result = await Publisher(SupervisionDetail.Full).PublishAsync(Full, TestContext.Current.CancellationToken);

        // Assert
        WorkStatusPublished published = result.Should().BeOfType<WorkStatusPublished>().Subject;
        EnvelopeResult onBus = await new EnvelopeSigner(_store).ParseAndVerifyAsync(_checkout.Read(published.Path), Acme, TestContext.Current.CancellationToken);
        onBus.IsAccepted.Should().BeTrue(onBus.Rejection?.ToString());
        onBus.Envelope!.Header.From.Should().Be(HomeLaptop);
        onBus.Envelope.Header.To.Should().Be(MachineName.Central);
        onBus.Envelope.Header.Created.Should().Be(T);
        onBus.Envelope.Status.Should().Be(new StatusFields("calizr", "5b3fd3ac", Since, T, "feature/12-booking-pool", "9f2c1ab", 3, "_specs/12-booking-pool.md", "slice 2"));
        published.Path.Should().Be(BusPaths.EnvelopeFile(BusPaths.Status(Acme, HomeLaptop), published.Id));
    }

    [Fact]
    public async Task PublishAsync_DetailFixed_FrontMatterHoldsOnlyProjectSessionSinceActivity()
    {
        // Act
        WorkStatusResult result = await Publisher(SupervisionDetail.Fixed).PublishAsync(Full, TestContext.Current.CancellationToken);

        // Assert
        WorkStatusPublished published = result.Should().BeOfType<WorkStatusPublished>().Subject;
        string text = Encoding.UTF8.GetString(_checkout.Read(published.Path));
        var keys = text.Split("\n---\n", 2)[0].Split('\n').Skip(1).Select(l => l[..l.IndexOf(':', StringComparison.Ordinal)]).ToList();
        keys.Should().Equal("activity", "created", "from", "id", "key_id", "priority", "project", "schema", "session", "sig", "since", "tenant", "to", "type");
        text.Should().NotContain("booking-pool").And.NotContain("9f2c1ab").And.NotContain("slice 2");
    }

    [Theory]
    [MemberData(nameof(SecretSamples))]
    public async Task PublishAsync_SecretShapedBranch_Refused_NothingSignedOrWritten_TextNowhere(string name, string sample)
    {
        // Act
        WorkStatusResult result = await Publisher(SupervisionDetail.Full).PublishAsync(Full with { Branch = sample }, TestContext.Current.CancellationToken);

        // Assert
        result.Should().Be(new WorkStatusRefused("branch", "matches secret pattern " + name));
        _processes.Calls.Should().BeEmpty();
        _provider.ReceivedCalls().Should().BeEmpty();
        _checkout.FilesUnder(BusPaths.Status(Acme, HomeLaptop)).Should().BeEmpty();
        _log.Messages.Should().NotContain(m => m.Contains(sample, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("project")]
    [InlineData("session")]
    [InlineData("head")]
    [InlineData("spec")]
    [InlineData("step")]
    public async Task PublishAsync_SecretShapedValueInAnyTextField_RefusedNamingTheField(string field)
    {
        // Arrange: one sample that every pattern file row for a GitHub token matches
        (string name, string sample) = FirstSample();
        WorkStatusRequest request = field switch
        {
            "project" => Full with { Project = sample },
            "session" => Full with { Session = sample },
            "head" => Full with { Head = sample },
            "spec" => Full with { Spec = sample },
            _ => Full with { Step = sample },
        };

        // Act
        WorkStatusResult result = await Publisher(SupervisionDetail.Full).PublishAsync(request, TestContext.Current.CancellationToken);

        // Assert
        result.Should().Be(new WorkStatusRefused(field, "matches secret pattern " + name));
        _processes.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task PublishAsync_DetailFixed_SecretShapedBranchIsDroppedNotRefused()
    {
        // Arrange
        (_, string sample) = FirstSample();

        // Act
        WorkStatusResult result = await Publisher(SupervisionDetail.Fixed).PublishAsync(Full with { Branch = sample }, TestContext.Current.CancellationToken);

        // Assert
        WorkStatusPublished published = result.Should().BeOfType<WorkStatusPublished>().Subject;
        Encoding.UTF8.GetString(_checkout.Read(published.Path)).Should().NotContain(sample);
    }

    [Fact]
    public async Task PublishAsync_HooksOff_SkippedNothingSignedOrWritten()
    {
        // Act
        WorkStatusResult result = await Publisher(SupervisionDetail.Full, hooksOff: true).PublishAsync(Full, TestContext.Current.CancellationToken);

        // Assert
        result.Should().Be(new WorkStatusSkipped("hooks off"));
        _processes.Calls.Should().BeEmpty();
        _provider.ReceivedCalls().Should().BeEmpty();
        _checkout.FilesUnder(BusPaths.Status(Acme, HomeLaptop)).Should().BeEmpty();
    }

    [Fact]
    public async Task PublishAsync_SupervisionDisabled_SkippedNothingWritten()
    {
        // Act
        WorkStatusResult result = await Publisher(SupervisionDetail.Full, enabled: false).PublishAsync(Full, TestContext.Current.CancellationToken);

        // Assert
        result.Should().Be(new WorkStatusSkipped("supervision disabled"));
        _processes.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task PublishAsync_FieldOfWrongShape_RefusedNamingTheField_NothingWritten()
    {
        // Act
        WorkStatusResult result = await Publisher(SupervisionDetail.Full).PublishAsync(Full with { Session = "short" }, TestContext.Current.CancellationToken);

        // Assert
        result.Should().BeOfType<WorkStatusRefused>().Which.Field.Should().Be("session");
        _processes.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task PublishAsync_SigningKeyAbsent_FailedAtSign_NothingWritten()
    {
        // Arrange
        _store = new InMemorySecretStore();

        // Act
        WorkStatusResult result = await Publisher(SupervisionDetail.Full).PublishAsync(Full, TestContext.Current.CancellationToken);

        // Assert
        result.Should().BeOfType<WorkStatusFailed>().Which.Stage.Should().Be("sign");
        _processes.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task RemoveAsync_HooksOff_Skipped()
    {
        // Act
        WorkStatusResult result = await Publisher(SupervisionDetail.Full, hooksOff: true).RemoveAsync("5b3fd3ac", TestContext.Current.CancellationToken);

        // Assert
        result.Should().Be(new WorkStatusSkipped("hooks off"));
        _processes.Calls.Should().BeEmpty();
    }

    private static (string Name, string Sample) FirstSample()
    {
        (string name, string sample) = SecretPatternsTests.SecretSamples().First().Data;
        return (name, sample);
    }

    private WorkStatusPublisher Publisher(SupervisionDetail detail, bool hooksOff = false, bool enabled = true)
    {
        var git = new GitClient(_processes, new GitClientOptions { Identity = new GitIdentity("zyggy (home-laptop)", "home-laptop@zyggy.org") }, _clock);
        var signer = new EnvelopeSigner(_store);
        var bus = new BusRepository(git, _provider, new BusIdentity(Acme, HomeLaptop), _checkout.Root, signer, _clock, () => 0.5, new RecordingLogger<BusRepository>());
        SecretPatterns patterns = SecretPatterns.Load(Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt")).Patterns!;
        return new WorkStatusPublisher(bus, signer, TestKeys.Acme1, patterns, new WorkStatusOptions(detail, enabled, hooksOff), _clock, _log);
    }
}
