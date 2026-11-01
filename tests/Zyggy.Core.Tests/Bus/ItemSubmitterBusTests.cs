using System.Text;

using Microsoft.Extensions.Logging;
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

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Bus;

/// <summary>
/// Spec 41 AC-13, AC-15..AC-17, AC-25..AC-27 through the seams: <c>work-laptop</c> (no OneDrive folder) sends a passed file as a signed
/// <c>via: bus</c> item into its own <c>items/</c> folder; every refusal and failure is a named outcome with nothing written.
/// </summary>
public sealed class ItemSubmitterBusTests : IAsyncLifetime
{
    private const string Notes = "# Staging restore\n\nRestore the staging database from Sunday's snapshot.\n";

    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName WorkLaptop = MachineName.Parse("work-laptop");

    private static readonly DateTimeOffset T = new(2026, 10, 31, 14, 5, 0, TimeSpan.Zero);

    private readonly TempCheckout _checkout = new(Acme, WorkLaptop);

    private readonly RecordingProcessRunner _processes = new();

    private readonly IBusProvider _provider = Substitute.For<IBusProvider>();

    private readonly RecordingLogger<ItemSubmitter> _log = new();

    private readonly FakeTimeProvider _clock = new(T);

    private readonly string _home = Path.Combine(Path.GetTempPath(), "zyggy-ut-submit", Guid.NewGuid().ToString("N"));

    private InMemorySecretStore _store = null!;

    private static string ItemsFolder => BusPaths.Items(Acme, WorkLaptop);

    public async ValueTask InitializeAsync()
    {
        _store = await TestKeys.StoreWithBothAsync(CancellationToken.None);
        _provider.PullAsync(Arg.Any<CancellationToken>()).Returns(new BusSyncResult(true, string.Empty));
        _provider.PushAsync(Arg.Any<CancellationToken>()).Returns(new PushSucceeded());
    }

    public ValueTask DisposeAsync()
    {
        _checkout.Dispose();
        if (Directory.Exists(_home))
        {
            Directory.Delete(_home, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task SubmitAsync_NoOneDriveFolder_ViaBusItemPublished_SubmittedWithItemsPath_RouteBus_FileVerifies()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string source = Source("restore-notes.md", Notes);

        // Act
        ItemSubmission submission = await Submitter().SubmitAsync(Request(source), ct);

        // Assert
        var submitted = submission.Result.Should().BeOfType<Submitted>().Subject;
        submitted.RelativePath.Should().Be(BusPaths.EnvelopeFile(ItemsFolder, submitted.Id)).And.StartWith("tenants/acme/nodes/work-laptop/items/");
        submission.Route.Should().Be(ItemRoute.Bus);
        submission.ItemPath.Should().Be("calizr/staging-restore-notes.txt");
        submission.Name.Should().Be("Staging restore notes");
        submission.MediaType.Should().Be("text/markdown");
        submission.Size.Should().Be(Encoding.UTF8.GetByteCount(Notes));
        EnvelopeResult check = await new EnvelopeSigner(_store).ParseAndVerifyAsync(_checkout.Read(submitted.RelativePath), Acme, ct);
        check.IsAccepted.Should().BeTrue(check.Rejection?.ToString());
        check.Envelope!.Header.To.Should().Be(MachineName.Central);
        check.Envelope.Item!.Via.Should().Be(ItemRoute.Bus);
        check.Envelope.Item.SourceName.Should().Be("restore-notes.md");
        ItemBody.TryDecode(check.Envelope, out byte[]? decoded).Should().BeTrue();
        decoded.Should().Equal(Encoding.UTF8.GetBytes(Notes));
        _processes.CallsOf("commit").Should().ContainSingle().Which.StandardInput.Should().StartWith($"acme item {submitted.Id.Value} work-laptop→central");
        _log.AtLevel(LogLevel.Information).Select(e => e.Message).Should().ContainSingle().Which.Should().Be($"published item {submitted.Id.Value} to central via bus");
    }

    [Fact]
    public async Task SubmitAsync_PushRejectedFourTimes_SubmitDeferred()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        _provider.PushAsync(Arg.Any<CancellationToken>()).Returns(new Rejected("fetch first"));

        // Act
        Task<ItemSubmission> task = Submitter().SubmitAsync(Request(Source("restore-notes.md", Notes)), ct);
        while (!task.IsCompleted)
        {
            _clock.Advance(TimeSpan.FromMilliseconds(500));
            await Task.Delay(1, ct);
        }

        ItemSubmission submission = await task;

        // Assert
        var deferred = submission.Result.Should().BeOfType<SubmitDeferred>().Subject;
        deferred.PushAttempts.Should().Be(4);
        submission.Route.Should().Be(ItemRoute.Bus);
        _checkout.Exists(deferred.RelativePath).Should().BeTrue("the commit stays local");
    }

    public static TheoryData<string, string> FailedStages() => new()
    {
        { "key-absent", "sign" },
        { "store-unavailable", "secret-store" },
        { "lock-held", "lock" },
        { "pull-fails", "pull" },
    };

    [Theory]
    [MemberData(nameof(FailedStages))]
    public async Task SubmitAsync_KeyAbsent_StoreUnavailable_LockHeld_PullFails_SubmitFailedStage_NothingWritten(string scenario, string stage)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string source = Source("restore-notes.md", Notes);
        Directory.CreateDirectory(Path.Combine(_checkout.Root, ".git"));
        using IDisposable? other = scenario == "lock-held"
            ? await BusCheckoutLock.AcquireAsync(_checkout.Root, BusCheckoutLock.WriteLockName, TimeSpan.Zero, _clock, ct)
            : null;
        _provider.PullAsync(Arg.Any<CancellationToken>()).Returns(new BusSyncResult(scenario != "pull-fails", scenario == "pull-fails" ? "fatal: unable to access" : string.Empty));
        ItemSubmitter submitter = scenario switch
        {
            "key-absent" => Submitter(signWith: KeyId.Parse("acme/9")),
            "store-unavailable" => Submitter(store: new ThrowingSecretStore()),
            _ => Submitter(),
        };

        // Act
        Task<ItemSubmission> task = submitter.SubmitAsync(Request(source), ct);
        for (int i = 0; i < 200 && !task.IsCompleted; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(5, ct);
        }

        ItemSubmission submission = await task;

        // Assert
        submission.Result.Should().BeOfType<SubmitFailed>().Which.Stage.Should().Be(stage);
        _processes.CallsOf("add").Should().BeEmpty();
        _processes.CallsOf("commit").Should().BeEmpty();
        _checkout.FilesUnder(ItemsFolder).Should().BeEmpty();
    }

    [Fact]
    public async Task SubmitAsync_RefusedByCheck_ItemRefused_SignerNeverCalled_BusNeverCalled()
    {
        // Arrange
        var store = Substitute.For<ISecretStore>();
        string source = Source("empty.txt", string.Empty);

        // Act
        ItemSubmission submission = await Submitter(store: store).SubmitAsync(Request(source), TestContext.Current.CancellationToken);

        // Assert
        submission.Result.Should().Be(new ItemRefused(ArchiveRefusal.Empty, null));
        submission.Route.Should().BeNull();
        store.ReceivedCalls().Should().BeEmpty();
        _provider.ReceivedCalls().Should().BeEmpty();
        _processes.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task SubmitAsync_IndexLineTooLong_ItemUsage_NothingSigned()
    {
        // Arrange
        var store = Substitute.For<ISecretStore>();
        string source = Source("restore-notes.md", Notes);
        var request = new ItemRequest(
            Slug.Parse("calizr-staging-environment-and-the-restore-of-its-database"),
            string.Concat(Enumerable.Repeat("Staging restore notes ", 5))[..100],
            string.Concat(Enumerable.Repeat("How the staging database was restored ", 4))[..149],
            source,
            Slug.Parse("staging-restore-notes-for-the-database-of-the-calizr-project"),
            viaBus: false);

        // Act
        ItemSubmission submission = await Submitter(store: store).SubmitAsync(request, TestContext.Current.CancellationToken);

        // Assert
        submission.Result.Should().BeOfType<ItemUsage>().Which.Message.Should().StartWith("the index line would exceed 400 characters");
        store.ReceivedCalls().Should().BeEmpty();
        _provider.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task SubmitAsync_SecretLine_ItemRefusedSecretPattern_TextInNoLogLine()
    {
        // Arrange
        const string secret = "AKIAIOSFODNN7EXAMPLE";
        string source = Source("notes.txt", "Staging notes\nkey " + secret + "\n");

        // Act
        ItemSubmission submission = await Submitter().SubmitAsync(Request(source), TestContext.Current.CancellationToken);

        // Assert
        submission.Result.Should().Be(new ItemRefused(ArchiveRefusal.SecretPattern, "aws-access-key (line 2)"));
        _processes.Calls.Should().BeEmpty();
        _checkout.FilesUnder(ItemsFolder).Should().BeEmpty();
        _log.Messages.Should().NotContain(m => m.Contains(secret, StringComparison.Ordinal));
        SecretSweep.AssertAbsent(_log.Messages);
    }

    [Fact]
    public async Task SubmitAsync_SerializerCorruptsOneBodyByte_SubmitRefusedBody()
    {
        // Arrange
        ItemSubmitter submitter = Submitter(serializer: e => CorruptFirstBodyByte(EnvelopeWriter.Serialize(e)));

        // Act
        ItemSubmission submission = await submitter.SubmitAsync(Request(Source("restore-notes.md", Notes)), TestContext.Current.CancellationToken);

        // Assert
        submission.Result.Should().BeOfType<SubmitRefused>().Which.Field.Should().Be("body");
        _processes.Calls.Should().BeEmpty();
        _checkout.FilesUnder(ItemsFolder).Should().BeEmpty();
        _log.AtLevel(LogLevel.Warning).Select(e => e.Message).Should().ContainSingle().Which.Should().Be("refusing item: the envelope does not round-trip (body)");
    }

    [Fact]
    public async Task SubmitAsync_SerializerCorruptsName_SubmitRefusedNameOrSig()
    {
        // Arrange
        ItemSubmitter submitter = Submitter(serializer: e => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(EnvelopeWriter.Serialize(e)).Replace("name: Staging restore notes", "name: Staging restore notez", StringComparison.Ordinal)));

        // Act
        ItemSubmission submission = await submitter.SubmitAsync(Request(Source("restore-notes.md", Notes)), TestContext.Current.CancellationToken);

        // Assert
        submission.Result.Should().BeOfType<SubmitRefused>().Which.Field.Should().BeOneOf("name", "sig");
        _processes.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task SubmitAsync_DlpEnabledOnWorkLaptop_NoExtraRule()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string nodeJson = Path.Combine(_home, "config", "node.json");
        Directory.CreateDirectory(Path.GetDirectoryName(nodeJson)!);
        File.WriteAllText(nodeJson, $$"""
            {
              "tenant": "acme",
              "machine": "work-laptop",
              "signWith": "acme/1",
              "dlp": { "enabled": true },
              "bus": { "owner": "acme-org", "repo": "acme-bus", "branch": "main", "localPath": "{{_checkout.Root.Replace('\\', '/')}}", "remote": "https://example.invalid/acme-org/acme-bus.git" },
              "secrets": { "store": "file", "root": "{{Path.Combine(_home, "secrets").Replace('\\', '/')}}" }
            }
            """);
        NodeConfigurationResult read = NodeConfigurationReader.Read(nodeJson);
        read.IsOk.Should().BeTrue(read.ToString());

        // Act
        ItemSubmission submission = await Submitter(node: read.Configuration, nodeJsonPath: nodeJson).SubmitAsync(Request(Source("restore-notes.md", Notes)), ct);

        // Assert
        submission.Result.Should().BeOfType<Submitted>();
        submission.Route.Should().Be(ItemRoute.Bus);
    }

    private static byte[] CorruptFirstBodyByte(byte[] bytes)
    {
        string text = Encoding.ASCII.GetString(bytes);
        int body = text.IndexOf("\n---\n", text.IndexOf('\n', StringComparison.Ordinal), StringComparison.Ordinal) + 5;
        byte[] copy = [.. bytes];
        copy[body] = copy[body] == (byte)'A' ? (byte)'B' : (byte)'A';
        return copy;
    }

    private static ItemRequest Request(string source) =>
        new(Slug.Parse("calizr"), "Staging restore notes", "How the staging database was restored", source, Slug.Parse("staging-restore-notes"), viaBus: false);

    private string Source(string name, string text)
    {
        string path = Path.Combine(_home, "Downloads", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(text));
        return path;
    }

    private NodeConfiguration Node() =>
        new(
            Acme,
            WorkLaptop,
            TestKeys.Acme1,
            new BusSettings("acme-org", "acme-bus", "main", _checkout.Root, "https://example.invalid/acme-org/acme-bus.git"),
            new PollSettings(),
            new SecretsSettings(SecretStoreKind.File, Path.Combine(_home, "secrets")))
        {
            NodeDir = Path.Combine(_home, "node"),
        };

    private ItemSubmitter Submitter(ISecretStore? store = null, Func<Model, byte[]>? serializer = null, KeyId? signWith = null, NodeConfiguration? node = null, string? nodeJsonPath = null)
    {
        var git = new GitClient(_processes, new GitClientOptions { Identity = new GitIdentity("zyggy (work-laptop)", "work-laptop@zyggy.org") }, _clock);
        var signer = new EnvelopeSigner(store ?? _store);
        var bus = new BusRepository(git, _provider, new BusIdentity(Acme, WorkLaptop), _checkout.Root, signer, _clock, () => 0, new RecordingLogger<BusRepository>());
        var patterns = SecretPatterns.Load(Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt")).Patterns!;
        node ??= Node();
        var env = new Dictionary<string, string?>(StringComparer.Ordinal) { ["HOME"] = _home };
        var deny = SourceDenyList.ForNode(env, node, nodeJsonPath ?? Path.Combine(_home, "config", "node.json"));
        var submitter = new ItemSubmitter(bus, signer, signWith ?? TestKeys.Acme1, patterns, node, deny, _clock, _log);
        return serializer is null ? submitter : new ItemSubmitter(bus, signer, signWith ?? TestKeys.Acme1, patterns, node, deny, _clock, _log) { Serializer = serializer };
    }
}
