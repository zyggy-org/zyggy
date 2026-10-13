using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Git;
using Zyggy.Core.Processes;
using Zyggy.Core.Tenancy;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Bus;

/// <summary>The test double under the same contract suite as the product provider.</summary>
public sealed class LocalBareRepoBusProviderContractTests : BusProviderContractTests
{
    private ServiceProvider? _services;

    public override async ValueTask DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        await base.DisposeAsync();
    }

    protected override Task<IBusProvider> CreateAsync(BusRepoFixture bus, MachineName machine, CancellationToken ct)
    {
        _services = new ServiceCollection().AddProcessRunner().BuildServiceProvider();
        var git = new GitClient(_services.GetRequiredService<IProcessRunner>(), new GitClientOptions { Environment = bus.GitEnvironment }, _services.GetRequiredService<TimeProvider>());
        return Task.FromResult<IBusProvider>(new LocalBareRepoBusProvider(git, bus.BareDir, bus.CloneDir, "main"));
    }
}
