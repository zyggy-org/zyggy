using Zyggy.Core.Bus;
using Zyggy.Core.Tenancy;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Bus;

/// <summary>
/// Spec 04 AC-24: the <see cref="IBusProvider"/> contract, one fact per rule, run against the product provider and the test
/// double by the two sealed subclasses. Every assertion reads the bare repository; no rule may end in an exception.
/// </summary>
public abstract class BusProviderContractTests : IAsyncLifetime
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName Central = MachineName.Parse("central");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private IBusProvider? _provider;

    protected BusRepoFixture Bus { get; } = new(Acme);

    protected IBusProvider Provider => _provider ?? throw new InvalidOperationException("not initialised");

    public async ValueTask InitializeAsync()
    {
        await Bus.InitializeAsync();
        await Bus.SeedTenantAsync(Acme, [Central, HomeLaptop], CancellationToken.None);
        _provider = await CreateAsync(Bus, HomeLaptop, CancellationToken.None);
    }

    public virtual async ValueTask DisposeAsync()
    {
        await Bus.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ProbeHeadAsync_NullETag_IsChanged()
    {
        // Act
        HeadProbe probe = await Provider.ProbeHeadAsync(null, TestContext.Current.CancellationToken);

        // Assert
        probe.Should().BeOfType<Changed>().Which.ETag.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ProbeHeadAsync_SameETag_IsUnchanged()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var first = (Changed)await Provider.ProbeHeadAsync(null, ct);

        // Act
        HeadProbe second = await Provider.ProbeHeadAsync(first.ETag, ct);

        // Assert
        second.Should().BeOfType<Unchanged>();
    }

    [Fact]
    public async Task ProbeHeadAsync_AfterForeignPush_IsChangedWithNewETag()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var first = (Changed)await Provider.ProbeHeadAsync(null, ct);
        await Bus.AdvanceMainFromSenderAsync(ct);

        // Act
        HeadProbe probe = await Provider.ProbeHeadAsync(first.ETag, ct);

        // Assert
        probe.Should().BeOfType<Changed>().Which.ETag.Should().NotBe(first.ETag);
    }

    [Fact]
    public async Task PullAsync_AfterForeignPush_BringsCommitIntoClone()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await Bus.AdvanceMainFromSenderAsync(ct);

        // Act
        BusSyncResult result = await Provider.PullAsync(ct);

        // Assert
        result.Succeeded.Should().BeTrue(result.Detail);
        (await Bus.RunGitAsync(Bus.CloneDir, ["rev-parse", "HEAD"], ct)).Should().Be(await Bus.HeadShaAsync(ct));
    }

    [Fact]
    public async Task PushAsync_LocalCommit_LandsOnMain()
    {
        // Arrange: a commit made in the clone by the fixture's own git, so a GitClient bug cannot mask itself
        var ct = TestContext.Current.CancellationToken;
        string path = BusPaths.Context(Acme, HomeLaptop) + "/note.txt";
        await File.WriteAllTextAsync(BusPaths.Resolve(Bus.CloneDir, path), "local\n", ct);
        await Bus.RunGitAsync(Bus.CloneDir, ["add", "--all"], ct);
        await Bus.RunGitAsync(Bus.CloneDir, ["commit", "-q", "-m", "local note"], ct);

        // Act
        PushResult result = await Provider.PushAsync(ct);

        // Assert
        result.Should().BeOfType<Pushed>();
        (await Bus.ExistsOnMainAsync(path, ct)).Should().BeTrue();
        (await Bus.LastCommitSubjectAsync(ct)).Should().Be("local note");
    }

    [Fact]
    public async Task PushAsync_RemoteAhead_IsRejectedNotException()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string path = BusPaths.Context(Acme, HomeLaptop) + "/note.txt";
        await File.WriteAllTextAsync(BusPaths.Resolve(Bus.CloneDir, path), "local\n", ct);
        await Bus.RunGitAsync(Bus.CloneDir, ["add", "--all"], ct);
        await Bus.RunGitAsync(Bus.CloneDir, ["commit", "-q", "-m", "local note"], ct);
        await Bus.AdvanceMainFromSenderAsync(ct);
        string headBefore = await Bus.HeadShaAsync(ct);

        // Act
        PushResult result = await Provider.PushAsync(ct);

        // Assert
        result.Should().BeOfType<Rejected>();
        (await Bus.HeadShaAsync(ct)).Should().Be(headBefore);
        (await Bus.ExistsOnMainAsync(path, ct)).Should().BeFalse();
    }

    [Fact]
    public async Task AnyFailure_IsAResult()
    {
        // Arrange: the remote vanishes
        var ct = TestContext.Current.CancellationToken;
        Bus.DeleteBare();

        // Act
        BusSyncResult pull = await Provider.PullAsync(ct);
        PushResult push = await Provider.PushAsync(ct);
        HeadProbe probe = await Provider.ProbeHeadAsync(null, ct);

        // Assert
        pull.Succeeded.Should().BeFalse();
        push.Should().BeOfType<PushFailed>();
        probe.Should().BeOfType<Unreachable>();
    }

    protected abstract Task<IBusProvider> CreateAsync(BusRepoFixture bus, MachineName machine, CancellationToken ct);
}
