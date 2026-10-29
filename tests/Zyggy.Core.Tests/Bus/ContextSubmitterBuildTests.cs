using System.Text;

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

using ContextRequest = Zyggy.Core.Bus.ContextRequest;
using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Bus;

/// <summary>Spec 12 AC-2 in process: a stated fact becomes a signed context envelope to Central that reads back as itself, or is refused before anything is signed.</summary>
public sealed class ContextSubmitterBuildTests : IAsyncLifetime
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly DateTimeOffset T = new(2026, 10, 28, 10, 42, 7, TimeSpan.Zero);

    private static readonly string[] Section4ContextKeys = ["created", "from", "id", "key_id", "priority", "schema", "scope", "sig", "tenant", "to", "type"];

    private readonly TempCheckout _checkout = new(Acme, HomeLaptop);

    private readonly RecordingProcessRunner _processes = new();

    private readonly IBusProvider _provider = Substitute.For<IBusProvider>();

    private readonly RecordingLogger<ContextSubmitter> _log = new();

    private readonly FakeTimeProvider _clock = new(T);

    private InMemorySecretStore _store = null!;

    public static TheoryData<string, string> SecretSamples() => SecretPatternsTests.SecretSamples();

    public static TheoryData<string> BenignSamples() => SecretPatternsTests.BenignSamples();

    public async ValueTask InitializeAsync() => _store = await TestKeys.StoreWithBothAsync(CancellationToken.None);

    public ValueTask DisposeAsync()
    {
        _checkout.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task BuildAsync_StatedFact_SignedContextToCentralFromMeWithScopeAndOneLineBody()
    {
        // Act
        BuildResult result = await Submitter().BuildAsync(Stated("project:calizr", "The staging database is restored from Sunday's snapshot"), TestContext.Current.CancellationToken);

        // Assert
        Model envelope = result.Should().BeOfType<Built>().Subject.Envelope;
        envelope.Type.Should().Be(EnvelopeType.Context);
        envelope.Header.Tenant.Should().Be(Acme);
        envelope.Header.From.Should().Be(HomeLaptop);
        envelope.Header.To.Should().Be(MachineName.Central);
        envelope.Header.InReplyTo.Should().BeNull();
        envelope.Header.Priority.Should().Be(EnvelopePriority.Normal);
        envelope.Context!.Scope.Should().Be(new ContextScope(ContextScopeKind.Project, "calizr"));
        envelope.KeyId.Should().Be(TestKeys.Acme1);
        envelope.Signature.Should().MatchRegex("^hmac-sha256:[0-9a-f]{64}$");
        envelope.Header.Created.Should().Be(T);
        envelope.Header.Id.Timestamp.Should().Be(T);
        envelope.BodyText.Should().Be("- [stated] 2026-10-28 (project:calizr): The staging database is restored from Sunday's snapshot\n");
        _processes.Calls.Should().BeEmpty("building touches no checkout");
        _provider.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task BuildAsync_Bytes_FrontMatterKeysAreExactlyTheSection4ContextSet_InCanonicalOrder()
    {
        // Act
        BuildResult result = await Submitter().BuildAsync(Stated("machine", "fact"), TestContext.Current.CancellationToken);

        // Assert
        var built = result.Should().BeOfType<Built>().Subject;
        var text = Encoding.UTF8.GetString(built.Bytes);
        var keys = text.Split("\n---\n", 2)[0].Split('\n').Skip(1).Select(l => l[..l.IndexOf(':', StringComparison.Ordinal)]).ToList();
        keys.Should().Equal(Section4ContextKeys);
        EnvelopeResult check = await new EnvelopeSigner(_store).ParseAndVerifyAsync(built.Bytes, Acme, TestContext.Current.CancellationToken);
        check.IsAccepted.Should().BeTrue(check.Rejection?.ToString());
    }

    [Theory]
    [MemberData(nameof(SecretSamples))]
    public async Task BuildAsync_SecretShapedLine_BuildRefusedByPatternNamesPattern_TextNowhere_NothingSigned(string name, string sample)
    {
        // Act
        BuildResult result = await Submitter().BuildAsync(Lines(sample), TestContext.Current.CancellationToken);

        // Assert
        result.Should().Be(new BuildRefusedByPattern(name));
        _processes.Calls.Should().BeEmpty();
        _provider.ReceivedCalls().Should().BeEmpty();
        _log.Messages.Should().NotContain(m => m.Contains(sample, StringComparison.Ordinal));
        _log.Messages.Should().ContainSingle().Which.Should().Be($"refusing context: matches secret pattern {name}");
        SecretSweep.AssertAbsent(_log.Messages);
    }

    [Theory]
    [MemberData(nameof(BenignSamples))]
    public async Task BuildAsync_BenignSamples_Built(string sample)
    {
        // Act
        BuildResult result = await Submitter().BuildAsync(Lines(sample), TestContext.Current.CancellationToken);

        // Assert
        result.Should().BeOfType<Built>();
    }

    [Fact]
    public async Task BuildAsync_BodyOver64KiB_BuildRefusedBody()
    {
        // Arrange
        var request = new ContextRequest(new ContextScope(ContextScopeKind.General, null), [.. Enumerable.Repeat("- " + string.Join(" ", Enumerable.Repeat("lorem", 40)), 300)]);

        // Act
        BuildResult result = await Submitter().BuildAsync(request, TestContext.Current.CancellationToken);

        // Assert
        result.Should().BeOfType<BuildRefused>().Which.Field.Should().Be("body");
        _processes.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task BuildAsync_SerializerAltersScope_BuildRefusedScopeOrSig()
    {
        // Arrange: plan 08 A1 — a broken emitter writes a field differently from what was signed
        ContextSubmitter submitter = Submitter(serializer: e => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(EnvelopeWriter.Serialize(e)).Replace("scope: project:calizr", "scope: machine", StringComparison.Ordinal)));

        // Act
        BuildResult result = await submitter.BuildAsync(Stated("project:calizr", "fact"), TestContext.Current.CancellationToken);

        // Assert
        result.Should().BeOfType<BuildRefused>().Which.Field.Should().BeOneOf("scope", "sig");
        _processes.Calls.Should().BeEmpty();
        _provider.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task BuildAsync_KeyAbsent_ThrowsSigningFailed()
    {
        // Arrange
        ContextSubmitter submitter = Submitter(signWith: KeyId.Parse("acme/9"));

        // Act
        Func<Task> act = () => submitter.BuildAsync(Stated(null, "fact"), TestContext.Current.CancellationToken);

        // Assert
        await act.Should().ThrowAsync<SigningFailedException>();
        _processes.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task BuildAsync_StoreUnavailable_ThrowsSecretStoreUnavailable()
    {
        // Arrange
        ContextSubmitter submitter = Submitter(store: new ThrowingSecretStore());

        // Act
        Func<Task> act = () => submitter.BuildAsync(Stated(null, "fact"), TestContext.Current.CancellationToken);

        // Assert
        await act.Should().ThrowAsync<SecretStoreUnavailableException>();
    }

    [Fact]
    public async Task BuildAsync_NeverLogsTheFactOrTheKey()
    {
        // Arrange
        const string token = "fact-token-9c41-do-not-log";

        // Act
        await Submitter().BuildAsync(Stated(null, "Remember " + token), TestContext.Current.CancellationToken);

        // Assert
        _log.Messages.Should().NotContain(m => m.Contains(token, StringComparison.Ordinal));
        SecretSweep.AssertAbsent(_log.Messages);
    }

    private static ContextRequest Stated(string? scope, string fact)
    {
        ContextRequest.TryCreateStated(scope, fact, new DateOnly(2026, 10, 28), out var request).Should().BeNull();
        return request!;
    }

    private static ContextRequest Lines(params string[] lines) => new(new ContextScope(ContextScopeKind.General, null), lines);

    private ContextSubmitter Submitter(Func<Model, byte[]>? serializer = null, KeyId? signWith = null, ISecretStore? store = null)
    {
        var git = new GitClient(_processes, new GitClientOptions { Identity = TestGitIdentities.HomeLaptop }, _clock);
        var signer = new EnvelopeSigner(store ?? _store);
        var bus = new BusRepository(git, _provider, new BusIdentity(Acme, HomeLaptop), _checkout.Root, signer, _clock, () => 0, new RecordingLogger<BusRepository>());
        var patterns = SecretPatterns.Load(Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt")).Patterns!;
        var submitter = new ContextSubmitter(bus, signer, signWith ?? TestKeys.Acme1, patterns, new ContextEnvelopeOptions(), _clock, _log);
        return serializer is null ? submitter : new ContextSubmitter(bus, signer, signWith ?? TestKeys.Acme1, patterns, new ContextEnvelopeOptions(), _clock, _log) { Serializer = serializer };
    }
}
