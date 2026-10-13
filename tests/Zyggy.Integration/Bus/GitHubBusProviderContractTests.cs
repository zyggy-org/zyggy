using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Tenancy;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Bus;

/// <summary>The product provider under the contract suite: head check through <see cref="BareRepoHeadHandler"/>, pull and push through the real git.</summary>
public sealed class GitHubBusProviderContractTests : BusProviderContractTests
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

    protected override async Task<IBusProvider> CreateAsync(BusRepoFixture bus, MachineName machine, CancellationToken ct)
    {
        _services = TestHost.Build(bus, machine, await TestHost.StoreAsync(bus.Tenant, ct));
        IBusProvider provider = _services.GetRequiredService<IBusProvider>();
        provider.Should().BeOfType<GitHubBusProvider>();
        return provider;
    }
}
