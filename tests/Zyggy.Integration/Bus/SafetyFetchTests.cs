using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Bus;

/// <summary>Spec 07 AC-7 on the wire (the AC-19 primitive): the §5 item 6 safety fetch against the bare repository with the real <c>git</c>.</summary>
public sealed class SafetyFetchTests : IAsyncLifetime
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName Central = MachineName.Parse("central");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private readonly BusRepoFixture _bus = new(Acme);

    private InMemorySecretStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        await _bus.InitializeAsync();
        await _bus.SeedTenantAsync(Acme, [Central, HomeLaptop], CancellationToken.None);
        _store = await TestHost.StoreAsync(Acme, CancellationToken.None);
    }

    public ValueTask DisposeAsync() => _bus.DisposeAsync();

    [Fact]
    public async Task SafetyFetchAsync_SenderPushedBehindTheDoorbell_MovedThenPullSeesTheJob()
    {
        // Arrange: the node is in sync; then the sender pushes a job
        var ct = TestContext.Current.CancellationToken;
        await using ServiceProvider services = TestHost.Build(_bus, HomeLaptop, _store);
        BusRepository repository = services.GetRequiredService<BusRepository>();
        (await repository.PullAsync(ct)).Succeeded.Should().BeTrue();
        SeededJob job = await _bus.SeedSignedJobAsync(Central, HomeLaptop, TestKeys.Acme1, _store, "probe\n", ct: ct);

        // Act
        SafetyFetchResult fetch = await repository.SafetyFetchAsync(ct);
        BusSyncResult pull = await repository.PullAsync(ct);

        // Assert
        fetch.Outcome.Should().Be(SafetyFetchOutcome.Moved);
        fetch.Detail.Should().HaveLength(12);
        pull.Succeeded.Should().BeTrue(pull.Detail);
        (await repository.ListNewJobsAsync(ct)).Select(f => f.Id).Should().Equal(job.Id);
    }

    [Fact]
    public async Task SafetyFetchAsync_NothingPushed_Unchanged()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using ServiceProvider services = TestHost.Build(_bus, HomeLaptop, _store);
        BusRepository repository = services.GetRequiredService<BusRepository>();
        (await repository.PullAsync(ct)).Succeeded.Should().BeTrue();

        // Act
        SafetyFetchResult fetch = await repository.SafetyFetchAsync(ct);

        // Assert
        fetch.Outcome.Should().Be(SafetyFetchOutcome.Unchanged);
    }

    [Fact]
    public async Task SafetyFetchAsync_BareDeleted_FailedNoThrow()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using ServiceProvider services = TestHost.Build(_bus, HomeLaptop, _store);
        BusRepository repository = services.GetRequiredService<BusRepository>();
        _bus.DeleteBare();

        // Act
        SafetyFetchResult fetch = await repository.SafetyFetchAsync(ct);

        // Assert
        fetch.Outcome.Should().Be(SafetyFetchOutcome.Failed);
        fetch.Detail.Should().NotBeEmpty();
        SecretSweep.AssertAbsent([fetch.Detail]);
    }

    [Fact]
    public async Task SafetyFetchAsync_OwnPushedCommit_Unchanged()
    {
        // Arrange: the node's own claim is pushed, so HEAD and origin/main agree
        var ct = TestContext.Current.CancellationToken;
        await using ServiceProvider services = TestHost.Build(_bus, HomeLaptop, _store);
        BusRepository repository = services.GetRequiredService<BusRepository>();
        await _bus.SeedSignedJobAsync(Central, HomeLaptop, TestKeys.Acme1, _store, "probe\n", ct: ct);
        (await repository.PullAsync(ct)).Succeeded.Should().BeTrue();
        BusFile file = (await repository.ListNewJobsAsync(ct)).Single();
        var accepted = (AcceptedJob)await repository.VerifyIntakeAsync(file, ct);
        (await repository.ClaimAsync(accepted, ct)).Should().BeOfType<Pushed>();

        // Act
        SafetyFetchResult fetch = await repository.SafetyFetchAsync(ct);

        // Assert
        fetch.Outcome.Should().Be(SafetyFetchOutcome.Unchanged);
    }
}
