using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Bus;

/// <summary>Patch 0.8.1 (plan 04 Step 22): two writers in one checkout take turns instead of colliding.</summary>
public sealed class ConcurrentWritersTests : IAsyncLifetime
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
    public async Task TwoRepositoriesOneCheckout_ConcurrentSubmissions_AllPushedNoFailure()
    {
        // Arrange: two independent service providers (two "processes") on the same clone, each submitting five jobs at once
        var ct = TestContext.Current.CancellationToken;
        await using ServiceProvider first = TestHost.Build(_bus, HomeLaptop, _store);
        await using ServiceProvider second = TestHost.Build(_bus, HomeLaptop, _store);
        BusRepository a = first.GetRequiredService<BusRepository>();
        BusRepository b = second.GetRequiredService<BusRepository>();
        var jobs = new List<Zyggy.Core.Envelope.Envelope>();
        for (int i = 0; i < 10; i++)
        {
            jobs.Add(await TestEnvelopes.SignedJobAsync(HomeLaptop, Central, _store, TestKeys.Acme1, "probe\n", ct, EnvelopeId.New(DateTimeOffset.UtcNow.AddMilliseconds(i))));
        }

        // Act
        WriteOutcome[] outcomes = await Task.WhenAll(jobs.Select((job, i) => (i % 2 == 0 ? a : b).SubmitAsync(job, ct)));

        // Assert
        outcomes.Should().AllSatisfy(o => o.Should().BeOfType<Pushed>());
        foreach (var job in jobs)
        {
            (await _bus.ExistsOnMainAsync(BusPaths.EnvelopeFile(BusPaths.Jobs(Acme, Central), job.Header.Id), ct)).Should().BeTrue();
        }

        (await _bus.RunGitAsync(_bus.CloneDir, ["status", "--porcelain"], ct)).Should().BeEmpty();
        File.Exists(Path.Combine(_bus.CloneDir, ".git", "zyggy-bus.lock")).Should().BeFalse("the lock file is deleted when released");
    }

    [Fact]
    public async Task WriteAndPullConcurrently_NoCannotRebaseError()
    {
        // Arrange: one repository writes while another pulls in a loop on the same clone. The remote moves only now and
        // then, so the test exercises contention on the checkout, not push starvation by a foreign writer (that ends,
        // correctly, as Deferred and is covered by PushRetryTests).
        var ct = TestContext.Current.CancellationToken;
        await using ServiceProvider writer = TestHost.Build(_bus, HomeLaptop, _store);
        await using ServiceProvider puller = TestHost.Build(_bus, HomeLaptop, _store);
        BusRepository w = writer.GetRequiredService<BusRepository>();
        BusRepository p = puller.GetRequiredService<BusRepository>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pulls = new List<BusSyncResult>();
        Task pulling = Task.Run(async () =>
        {
            for (int round = 0; !stop.IsCancellationRequested; round++)
            {
                if (round % 5 == 0)
                {
                    try
                    {
                        await _bus.AdvanceMainFromSenderAsync(CancellationToken.None);
                    }
                    catch (InvalidOperationException)
                    {
                        // The foreign push lost the race against the writer's push (the remote refused a stale ref);
                        // a real machine would rebase and retry — here the next round simply tries again.
                    }
                }

                pulls.Add(await p.PullAsync(CancellationToken.None));
            }
        }, CancellationToken.None);

        // Act
        var outcomes = new List<WriteOutcome>();
        for (int i = 0; i < 4; i++)
        {
            outcomes.Add(await w.SubmitAsync(await TestEnvelopes.SignedJobAsync(HomeLaptop, Central, _store, TestKeys.Acme1, "probe\n", ct, EnvelopeId.New(DateTimeOffset.UtcNow.AddMilliseconds(i))), ct));
        }

        await stop.CancelAsync();
        await pulling;

        // Assert
        outcomes.Should().AllSatisfy(o => o.Should().BeOfType<Pushed>());
        pulls.Should().NotBeEmpty();
        pulls.Should().AllSatisfy(r => r.Succeeded.Should().BeTrue(r.Detail));
    }
}
