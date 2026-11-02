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

namespace Zyggy.Core.Tests.Bus;

/// <summary>
/// Spec 41 AC-15..AC-17, AC-19..AC-22, AC-24 through the seams: <c>home-laptop</c> with <c>archive.oneDriveFolder</c> configured places
/// the bytes in a substituted drop folder before signing and publishes a <c>via: onedrive</c> item; an unavailable folder or a failed
/// placement publishes nothing and never falls back to the bus; <c>--via bus</c>, a refusal or a failed pull never touch the folder.
/// </summary>
public sealed class ItemSubmitterOneDriveTests : IAsyncLifetime
{
    private const string Notes = "# Staging restore\n\nRestore the staging database from Sunday's snapshot.\n";

    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly DateTimeOffset T = new(2026, 10, 31, 14, 5, 0, TimeSpan.Zero);

    private readonly TempCheckout _checkout = new(Acme, HomeLaptop);

    private readonly RecordingProcessRunner _processes = new();

    private readonly IBusProvider _provider = Substitute.For<IBusProvider>();

    private readonly FakeTimeProvider _clock = new(T);

    private readonly string _home = Path.Combine(Path.GetTempPath(), "zyggy-ut-onedrive", Guid.NewGuid().ToString("N"));

    private readonly List<string> _events = [];

    private readonly List<string> _factoryRoots = [];

    private readonly ScriptedDropFolder _drop;

    private RecordingSecretStore _store = null!;

    public ItemSubmitterOneDriveTests() => _drop = new ScriptedDropFolder(_events);

    private static string ItemsFolder => BusPaths.Items(Acme, HomeLaptop);

    private string Folder => Path.Combine(_home, "OneDrive", "Zyggy", "archive");

    public async ValueTask InitializeAsync()
    {
        _store = new RecordingSecretStore(await TestKeys.StoreWithBothAsync(CancellationToken.None), _events);
        _provider.PullAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _events.Add("pull");
            return new BusSyncResult(true, string.Empty);
        });
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
    public async Task SubmitAsync_FolderAvailable_PlacedThenViaOneDriveItemPublished_EmptyBody_DriveNameSlugTxt_RouteOneDrive()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string source = Source("restore-notes.md", Notes);

        // Act
        ItemSubmission submission = await Submitter().SubmitAsync(Request(source), ct);

        // Assert
        var submitted = submission.Result.Should().BeOfType<Submitted>().Subject;
        submitted.RelativePath.Should().Be(BusPaths.EnvelopeFile(ItemsFolder, submitted.Id));
        submission.Route.Should().Be(ItemRoute.OneDrive);
        submission.ItemPath.Should().Be("calizr/staging-restore-notes.txt");
        _factoryRoots.Should().Equal(Folder);
        _drop.Placed.Should().ContainSingle().Which.Should().Be(("calizr", "staging-restore-notes.txt", Notes));
        _events.Should().ContainInOrder("pull", "probe", "place", "store");
        EnvelopeResult check = await new EnvelopeSigner(_store).ParseAndVerifyAsync(_checkout.Read(submitted.RelativePath), Acme, ct);
        check.IsAccepted.Should().BeTrue(check.Rejection?.ToString());
        check.Envelope!.Item!.Via.Should().Be(ItemRoute.OneDrive);
        check.Envelope.Item.DriveName.Should().Be("staging-restore-notes.txt");
        check.Envelope.Item.Size.Should().Be(Encoding.UTF8.GetByteCount(Notes));
        check.Envelope.Body.Length.Should().Be(0);
        _processes.CallsOf("commit").Should().ContainSingle().Which.StandardInput.Should().StartWith($"acme item {submitted.Id.Value} home-laptop→central");
    }

    [Fact]
    public async Task SubmitAsync_IdenticalFileAlreadyThere_NothingRewritten_EnvelopePublished()
    {
        // Arrange
        _drop.Placement = new DropIdentical();

        // Act
        ItemSubmission submission = await Submitter().SubmitAsync(Request(Source("restore-notes.md", Notes)), TestContext.Current.CancellationToken);

        // Assert
        submission.Result.Should().BeOfType<Submitted>();
        submission.Route.Should().Be(ItemRoute.OneDrive);
        _checkout.FilesUnder(ItemsFolder).Should().ContainSingle();
    }

    [Fact]
    public async Task SubmitAsync_DifferentFileThere_ItemRefusedSlugTakenOneDrive_NoEnvelope()
    {
        // Arrange
        _drop.Placement = new DropTaken();

        // Act
        ItemSubmission submission = await Submitter().SubmitAsync(Request(Source("restore-notes.md", Notes)), TestContext.Current.CancellationToken);

        // Assert
        submission.Result.Should().Be(new ItemRefused(ArchiveRefusal.SlugTaken, "onedrive"));
        _events.SkipWhile(e => e != "place").Should().Equal("place");
        _processes.CallsOf("add").Should().BeEmpty();
        _checkout.FilesUnder(ItemsFolder).Should().BeEmpty();
    }

    [Theory]
    [InlineData(nameof(DropProbe.Missing), "missing")]
    [InlineData(nameof(DropProbe.NotAFolder), "not_a_folder")]
    [InlineData(nameof(DropProbe.AccessDenied), "access_denied")]
    public async Task SubmitAsync_FolderMissingNotAFolderAccessDenied_OneDriveUnavailableWithReason_NothingPublished_NoFallback(string probe, string reason)
    {
        // Arrange
        _drop.ProbeResult = Enum.Parse<DropProbe>(probe);

        // Act
        ItemSubmission submission = await Submitter().SubmitAsync(Request(Source("restore-notes.md", Notes)), TestContext.Current.CancellationToken);

        // Assert
        submission.Result.Should().Be(new OneDriveUnavailable(reason, Folder));
        _drop.Placed.Should().BeEmpty();
        _processes.CallsOf("add").Should().BeEmpty();
        _processes.CallsOf("commit").Should().BeEmpty();
        _checkout.FilesUnder(ItemsFolder).Should().BeEmpty();
    }

    [Fact]
    public async Task SubmitAsync_PlaceFailed_OneDriveWriteFailed_NoEnvelope()
    {
        // Arrange
        _drop.Placement = new DropFailed("the re-read SHA-256 differs");

        // Act
        ItemSubmission submission = await Submitter().SubmitAsync(Request(Source("restore-notes.md", Notes)), TestContext.Current.CancellationToken);

        // Assert
        submission.Result.Should().Be(new OneDriveWriteFailed("the re-read SHA-256 differs"));
        _processes.CallsOf("add").Should().BeEmpty();
        _checkout.FilesUnder(ItemsFolder).Should().BeEmpty();
    }

    [Fact]
    public async Task SubmitAsync_ViaBusWithFolderConfigured_FolderNeverTouched_ViaBusItem()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;

        // Act
        ItemSubmission submission = await Submitter().SubmitAsync(Request(Source("restore-notes.md", Notes), viaBus: true), ct);

        // Assert
        var submitted = submission.Result.Should().BeOfType<Submitted>().Subject;
        submission.Route.Should().Be(ItemRoute.Bus);
        _factoryRoots.Should().BeEmpty();
        _events.Should().NotContain("probe").And.NotContain("place");
        EnvelopeResult check = await new EnvelopeSigner(_store).ParseAndVerifyAsync(_checkout.Read(submitted.RelativePath), Acme, ct);
        check.Envelope!.Item!.Via.Should().Be(ItemRoute.Bus);
    }

    [Fact]
    public async Task SubmitAsync_FolderAbsentFromNodeJson_FolderFactoryNeverCalled()
    {
        // Arrange
        NodeConfiguration node = Node() with { Archive = new ArchiveSettings() };

        // Act
        ItemSubmission submission = await Submitter(node).SubmitAsync(Request(Source("restore-notes.md", Notes)), TestContext.Current.CancellationToken);

        // Assert
        submission.Result.Should().BeOfType<Submitted>();
        submission.Route.Should().Be(ItemRoute.Bus);
        _factoryRoots.Should().BeEmpty();
    }

    [Fact]
    public async Task SubmitAsync_RefusedByCheck_FolderNeverTouched()
    {
        // Arrange
        string source = Source("notes.txt", "Staging notes\nkey AKIAIOSFODNN7EXAMPLE\n");

        // Act
        ItemSubmission submission = await Submitter().SubmitAsync(Request(source), TestContext.Current.CancellationToken);

        // Assert
        submission.Result.Should().BeOfType<ItemRefused>().Which.Refusal.Should().Be(ArchiveRefusal.SecretPattern);
        _factoryRoots.Should().BeEmpty();
        _events.Should().NotContain("probe").And.NotContain("place");
    }

    [Fact]
    public async Task SubmitAsync_PullFails_FolderNeverTouched()
    {
        // Arrange
        _provider.PullAsync(Arg.Any<CancellationToken>()).Returns(new BusSyncResult(false, "fatal: unable to access"));
        Directory.CreateDirectory(Path.Combine(_checkout.Root, ".git"));

        // Act
        ItemSubmission submission = await Submitter().SubmitAsync(Request(Source("restore-notes.md", Notes)), TestContext.Current.CancellationToken);

        // Assert
        submission.Result.Should().BeOfType<SubmitFailed>().Which.Stage.Should().Be("pull");
        _factoryRoots.Should().BeEmpty();
        _events.Should().NotContain("probe").And.NotContain("place");
    }

    private static ItemRequest Request(string source, bool viaBus = false) =>
        new(Slug.Parse("calizr"), "Staging restore notes", "How the staging database was restored", source, Slug.Parse("staging-restore-notes"), viaBus);

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
            HomeLaptop,
            TestKeys.Acme1,
            new BusSettings("acme-org", "acme-bus", "main", _checkout.Root, "https://example.invalid/acme-org/acme-bus.git"),
            new PollSettings(),
            new SecretsSettings(SecretStoreKind.File, Path.Combine(_home, "secrets")))
        {
            NodeDir = Path.Combine(_home, "node"),
            Archive = new ArchiveSettings { OneDriveFolder = Folder },
        };

    private ItemSubmitter Submitter(NodeConfiguration? node = null)
    {
        var git = new GitClient(_processes, new GitClientOptions { Identity = TestGitIdentities.HomeLaptop }, _clock);
        var signer = new EnvelopeSigner(_store);
        var bus = new BusRepository(git, _provider, new BusIdentity(Acme, HomeLaptop), _checkout.Root, signer, _clock, () => 0, new RecordingLogger<BusRepository>());
        var patterns = SecretPatterns.Load(Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt")).Patterns!;
        node ??= Node();
        var env = new Dictionary<string, string?>(StringComparer.Ordinal) { ["HOME"] = _home };
        var deny = SourceDenyList.ForNode(env, node, Path.Combine(_home, "config", "node.json"));
        return new ItemSubmitter(bus, signer, TestKeys.Acme1, patterns, node, deny, _clock, new RecordingLogger<ItemSubmitter>())
        {
            DropFolderFactory = root =>
            {
                _factoryRoots.Add(root);
                return _drop;
            },
        };
    }

    /// <summary>A hand-written drop folder (its <c>Place</c> takes a span, which a proxy cannot record): scripted answers, recorded calls.</summary>
    private sealed class ScriptedDropFolder(List<string> events) : IArchiveDropFolder
    {
        public DropProbe ProbeResult { get; set; } = DropProbe.Available;

        public DropPlacement Placement { get; set; } = new DropWritten();

        public List<(string Project, string DriveName, string Text)> Placed { get; } = [];

        public DropProbe Probe()
        {
            events.Add("probe");
            return ProbeResult;
        }

        public DropPlacement Place(Slug project, string driveName, ReadOnlySpan<byte> bytes)
        {
            events.Add("place");
            Placed.Add((project.Value, driveName, Encoding.UTF8.GetString(bytes)));
            return Placement;
        }

        public string TargetPath(Slug project, string driveName) => Path.Join("drop", project.Value, driveName);
    }

    /// <summary>Records every key read as <c>store</c> in the shared call log, so the test sees that the bytes are placed before signing.</summary>
    private sealed class RecordingSecretStore(ISecretStore inner, List<string> events) : ISecretStore
    {
        public Task<ReadOnlyMemory<byte>?> GetAsync(TenantId tenant, SecretName name, CancellationToken cancellationToken)
        {
            events.Add("store");
            return inner.GetAsync(tenant, name, cancellationToken);
        }

        public Task SetAsync(TenantId tenant, SecretName name, ReadOnlyMemory<byte> value, CancellationToken cancellationToken) => inner.SetAsync(tenant, name, value, cancellationToken);

        public Task<bool> RemoveAsync(TenantId tenant, SecretName name, CancellationToken cancellationToken) => inner.RemoveAsync(tenant, name, cancellationToken);

        public Task<IReadOnlyList<SecretName>> ListAsync(TenantId tenant, CancellationToken cancellationToken) => inner.ListAsync(tenant, cancellationToken);
    }
}
