using System.Text;

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
using Zyggy.Core.Tests.Memory;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Bus;

/// <summary>Spec 12 AC-18..AC-21 through the seams: each due spool ships as one signed envelope, is deleted only once it is on the bus or committed, and never carries a secret-shaped line.</summary>
public sealed class ContextFlusherTests : IAsyncLifetime
{
    private const string Session = "0b7c3d1e-4f5a-4b6c-8d7e-9f0a1b2c3d4e";

    private const string AwsKey = "AKIAABCDEFGHIJKLMNOP";

    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly ContextScope Calizr = ContextScope.Parse("project:calizr");

    private static readonly DateTimeOffset T = new(2026, 10, 28, 10, 42, 7, TimeSpan.Zero);

    private readonly string _nodeDir = Path.Combine(Path.GetTempPath(), "zyggy-unit", Guid.NewGuid().ToString("N"), "node");

    private readonly TempCheckout _checkout = new(Acme, HomeLaptop);

    private readonly RecordingProcessRunner _processes = new();

    private readonly IBusProvider _provider = Substitute.For<IBusProvider>();

    private readonly RecordingLogger<ContextFlusher> _log = new();

    private readonly FakeTimeProvider _clock = new(T);

    private readonly ContextEnvelopeOptions _options = new();

    private InMemorySecretStore _store = null!;

    private ContextSpool _spool = null!;

    public static TheoryData<string, string> SecretSamples() => SecretPatternsTests.SecretSamples();

    public async ValueTask InitializeAsync()
    {
        _store = await TestKeys.StoreWithBothAsync(CancellationToken.None);
        _spool = new ContextSpool(_nodeDir, Acme, _clock, _options);
        _provider.PullAsync(Arg.Any<CancellationToken>()).Returns(new BusSyncResult(true, string.Empty));
        _provider.PushAsync(Arg.Any<CancellationToken>()).Returns(new PushSucceeded());
    }

    public ValueTask DisposeAsync()
    {
        _checkout.Dispose();
        var root = Path.GetDirectoryName(_nodeDir)!;
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task FlushAsync_NamedSessionWithThreeLines_OneEnvelopeScopeAndLinesInOrder_SpoolDeleted()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Seed(Session, "- one", "- two", "- three");

        // Act
        var outcomes = await Flusher().FlushAsync(Session, ct);

        // Assert
        var submitted = outcomes.Should().ContainSingle().Which.Result.Should().BeOfType<Submitted>().Subject;
        Model context = await Parse(submitted.RelativePath, ct);
        context.Context!.Scope.Should().Be(Calizr);
        context.BodyText.Should().Be("- one\n- two\n- three\n");
        context.Header.From.Should().Be(HomeLaptop);
        context.Header.To.Should().Be(MachineName.Central);
        File.Exists(SpoolPath(Session)).Should().BeFalse();
        _processes.CallsOf("commit").Should().ContainSingle().Which.StandardInput.Should().StartWith($"acme context {submitted.Id.Value} home-laptop→central");
        _log.AtLevel(LogLevel.Information).Select(e => e.Message).Should().ContainSingle().Which.Should().Be($"shipped context {submitted.Id.Value} for session 0b7c3d1e");
    }

    [Fact]
    public async Task FlushAsync_StaleSpoolOfAnotherSession_ShippedToo_FreshOneLeftAlone()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Seed(Session, "- named");
        Seed("s2", "- stale", ageMinutes: 61);
        Seed("s3", "- fresh", ageMinutes: 5);

        // Act
        var outcomes = await Flusher().FlushAsync(Session, ct);

        // Assert
        outcomes.Select(o => o.File.SessionId).Should().Equal(Session, "s2");
        outcomes.Should().OnlyContain(o => o.Result is Submitted);
        File.Exists(SpoolPath(Session)).Should().BeFalse();
        File.Exists(SpoolPath("s2")).Should().BeFalse();
        File.Exists(SpoolPath("s3")).Should().BeTrue();
        _checkout.FilesUnder(BusPaths.Context(Acme, HomeLaptop)).Should().HaveCount(2);
    }

    [Fact]
    public async Task FlushAsync_SessionIdNull_StaleOnly()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Seed("s2", "- stale", ageMinutes: 61);
        Seed("s3", "- fresh", ageMinutes: 5);

        // Act
        var outcomes = await Flusher().FlushAsync(null, ct);

        // Assert
        outcomes.Select(o => o.File.SessionId).Should().Equal("s2");
        File.Exists(SpoolPath("s3")).Should().BeTrue();
    }

    [Fact]
    public async Task FlushAsync_PullFails_SubmitFailedPull_SpoolKeptUntouched()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Seed(Session, "- one");
        byte[] before = await File.ReadAllBytesAsync(SpoolPath(Session), ct);
        DateTime written = File.GetLastWriteTimeUtc(SpoolPath(Session));
        _provider.PullAsync(Arg.Any<CancellationToken>()).Returns(new BusSyncResult(false, "fatal: unable to access"));

        // Act
        var outcomes = await Flusher().FlushAsync(Session, ct);

        // Assert
        outcomes.Should().ContainSingle().Which.Result.Should().BeOfType<SubmitFailed>().Which.Stage.Should().Be("pull");
        (await File.ReadAllBytesAsync(SpoolPath(Session), ct)).Should().Equal(before);
        File.GetLastWriteTimeUtc(SpoolPath(Session)).Should().Be(written);
        _log.AtLevel(LogLevel.Warning).Select(e => e.Message).Should().ContainSingle().Which.Should().Be("context flush: spool of session 0b7c3d1e kept (pull)");

        // Act: the next flush, with the pull restored, ships it
        _provider.PullAsync(Arg.Any<CancellationToken>()).Returns(new BusSyncResult(true, string.Empty));
        var next = await Flusher().FlushAsync(Session, ct);

        // Assert
        next.Should().ContainSingle().Which.Result.Should().BeOfType<Submitted>();
        File.Exists(SpoolPath(Session)).Should().BeFalse();
    }

    [Fact]
    public async Task FlushAsync_PushRefusedFourTimes_SubmitDeferred_SpoolDeleted_CommitLocal()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Seed(Session, "- one");
        _provider.PushAsync(Arg.Any<CancellationToken>()).Returns(new Rejected("fetch first"));

        // Act
        Task<IReadOnlyList<FlushOutcome>> task = Flusher().FlushAsync(Session, ct);
        while (!task.IsCompleted)
        {
            _clock.Advance(TimeSpan.FromMilliseconds(500));
            await Task.Delay(1, ct);
        }

        var outcomes = await task;

        // Assert
        var deferred = outcomes.Should().ContainSingle().Which.Result.Should().BeOfType<SubmitDeferred>().Subject;
        deferred.PushAttempts.Should().Be(4);
        File.Exists(SpoolPath(Session)).Should().BeFalse("the commit is local; the next bus write pushes it");
        _checkout.Exists(deferred.RelativePath).Should().BeTrue();
    }

    [Fact]
    public async Task FlushAsync_OneSpoolFailsAnotherShips_BothOutcomesReturned()
    {
        // Arrange: the first spool's add fails, the second ships
        var ct = TestContext.Current.CancellationToken;
        Seed(Session, "- named");
        Seed("s2", "- stale", ageMinutes: 61);
        _processes.On("add", RecordingProcessRunner.Fail(128, "fatal: pathspec"), RecordingProcessRunner.Ok());
        _processes.On("checkout", RecordingProcessRunner.Fail(1, "error: pathspec did not match"));

        // Act
        var outcomes = await Flusher().FlushAsync(Session, ct);

        // Assert
        outcomes.Should().HaveCount(2);
        outcomes[0].Result.Should().BeOfType<SubmitFailed>().Which.Stage.Should().Be("add");
        outcomes[1].Result.Should().BeOfType<Submitted>();
        File.Exists(SpoolPath(Session)).Should().BeTrue();
        File.Exists(SpoolPath("s2")).Should().BeFalse();
    }

    [Fact]
    public async Task FlushAsync_HandEditedSecretLine_DroppedNamedRestShips()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Seed(Session, "- one", "- the key is " + AwsKey, "- three");

        // Act
        var outcomes = await Flusher().FlushAsync(Session, ct);

        // Assert
        var outcome = outcomes.Should().ContainSingle().Subject;
        outcome.DroppedPatterns.Should().Equal("aws-access-key");
        Model context = await Parse(outcome.Result.Should().BeOfType<Submitted>().Subject.RelativePath, ct);
        context.BodyText.Should().Be("- one\n- three\n");
        _log.Messages.Should().NotContain(m => m.Contains(AwsKey, StringComparison.Ordinal));
        _log.AtLevel(LogLevel.Warning).Select(e => e.Message).Should().Contain("context flush: line dropped (pattern aws-access-key)");
        _processes.Calls.SelectMany(SecretSweep.TextsOf).Should().NotContain(t => t != null && t.Contains(AwsKey, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(SecretSamples))]
    public async Task FlushAsync_EverySecretSample_Dropped(string name, string sample)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Seed(Session, "- keep", "- " + sample);

        // Act
        var outcomes = await Flusher().FlushAsync(Session, ct);

        // Assert
        var outcome = outcomes.Should().ContainSingle().Subject;
        outcome.DroppedPatterns.Should().Equal(name);
        Model context = await Parse(outcome.Result.Should().BeOfType<Submitted>().Subject.RelativePath, ct);
        context.BodyText.Should().Be("- keep\n");
    }

    [Fact]
    public async Task FlushAsync_SpoolLeftWithNoLine_NothingShippedSpoolDeleted()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Seed(Session, "- the key is " + AwsKey);

        // Act
        var outcomes = await Flusher().FlushAsync(Session, ct);

        // Assert
        var outcome = outcomes.Should().ContainSingle().Subject;
        outcome.Result.Should().BeNull();
        outcome.DroppedPatterns.Should().Equal("aws-access-key");
        File.Exists(SpoolPath(Session)).Should().BeFalse();
        _provider.ReceivedCalls().Should().BeEmpty();
        _processes.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task FlushAsync_EmptySpoolFileHeaderOnly_NothingShippedDeleted()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(_spool.Directory);
        await File.WriteAllTextAsync(SpoolPath(Session), "scope: project:calizr\n", ct);

        // Act
        var outcomes = await Flusher().FlushAsync(Session, ct);

        // Assert
        outcomes.Should().ContainSingle().Which.Result.Should().BeNull();
        File.Exists(SpoolPath(Session)).Should().BeFalse();
        _provider.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task FlushAsync_SelfCheckRefuses_SpoolKept()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Seed(Session, "- one");

        // Act
        var outcomes = await Flusher(serializer: e => [.. EnvelopeWriter.Serialize(e), (byte)'x']).FlushAsync(Session, ct);

        // Assert
        outcomes.Should().ContainSingle().Which.Result.Should().BeOfType<SubmitRefused>();
        File.Exists(SpoolPath(Session)).Should().BeTrue();
        _provider.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task FlushAsync_CapMarkerIsTheLastBodyLine_Shipped()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Seed(Session, "- one", _options.CapMarker);

        // Act
        var outcomes = await Flusher().FlushAsync(Session, ct);

        // Assert
        Model context = await Parse(outcomes.Single().Result.Should().BeOfType<Submitted>().Subject.RelativePath, ct);
        context.BodyText.Should().Be("- one\n" + _options.CapMarker + "\n");
    }

    [Fact]
    public async Task FlushAsync_DirectoryAbsent_EmptyOutcomes()
    {
        // Act
        var outcomes = await Flusher().FlushAsync(Session, TestContext.Current.CancellationToken);

        // Assert
        outcomes.Should().BeEmpty();
        _provider.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task FlushAsync_NeverLogsALineOrTheKey()
    {
        // Arrange
        const string token = "line-token-9c41-do-not-log";
        var ct = TestContext.Current.CancellationToken;
        Seed(Session, "- " + token);

        // Act
        await Flusher().FlushAsync(Session, ct);

        // Assert
        _log.Messages.Should().NotContain(m => m.Contains(token, StringComparison.Ordinal));
        SecretSweep.AssertAbsent(_log.Messages);
    }

    private string SpoolPath(string session) => Path.Combine(_spool.Directory, session + ".md");

    private void Seed(string session, params string[] lines) => Seed(session, lines, ageMinutes: 0);

    private void Seed(string session, string line, int ageMinutes) => Seed(session, [line], ageMinutes);

    private void Seed(string session, string[] lines, int ageMinutes)
    {
        Directory.CreateDirectory(_spool.Directory);
        File.WriteAllBytes(SpoolPath(session), new UTF8Encoding(false).GetBytes("scope: project:calizr\n" + string.Concat(lines.Select(l => l + "\n"))));
        File.SetLastWriteTimeUtc(SpoolPath(session), T.UtcDateTime.AddMinutes(-ageMinutes));
    }

    private async Task<Model> Parse(string relativePath, CancellationToken ct)
    {
        EnvelopeResult check = await new EnvelopeSigner(_store).ParseAndVerifyAsync(_checkout.Read(relativePath), Acme, ct);
        check.IsAccepted.Should().BeTrue(check.Rejection?.ToString());
        return check.Envelope!;
    }

    private ContextFlusher Flusher(Func<Model, byte[]>? serializer = null)
    {
        var git = new GitClient(_processes, new GitClientOptions { Identity = TestGitIdentities.HomeLaptop }, _clock);
        var signer = new EnvelopeSigner(_store);
        var bus = new BusRepository(git, _provider, new BusIdentity(Acme, HomeLaptop), _checkout.Root, signer, _clock, () => 0, new RecordingLogger<BusRepository>());
        var patterns = SecretPatterns.Load(Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt")).Patterns!;
        var submitter = new ContextSubmitter(bus, signer, TestKeys.Acme1, patterns, _options, _clock, new RecordingLogger<ContextSubmitter>());
        if (serializer is not null)
        {
            submitter = new ContextSubmitter(bus, signer, TestKeys.Acme1, patterns, _options, _clock, new RecordingLogger<ContextSubmitter>()) { Serializer = serializer };
        }

        return new ContextFlusher(_spool, submitter, patterns, _clock, _log);
    }
}
