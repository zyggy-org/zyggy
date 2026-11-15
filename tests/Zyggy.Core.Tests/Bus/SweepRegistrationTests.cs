using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Bus;

/// <summary>Spec 17 slice 2: <c>AddBusSweep</c> registers Central's sweep over the production composition root.</summary>
public sealed class SweepRegistrationTests
{
    [Fact]
    public void AddBusSweep_OverNodeServices_ResolvesOneSweep()
    {
        // Arrange
        var configuration = new NodeConfiguration(
            TenantId.Parse("acme"),
            MachineName.Central,
            TestKeys.Acme1,
            new BusSettings("acme-org", "acme-bus", "main", "/bus", "https://example.invalid/acme-org/acme-bus.git"),
            new PollSettings(),
            new SecretsSettings(SecretStoreKind.File, "/secrets"));
        using ServiceProvider provider = new ServiceCollection().AddNodeServices(configuration).AddBusSweep().BuildServiceProvider();

        // Act
        BusSweep sweep = provider.GetRequiredService<BusSweep>();

        // Assert
        provider.GetRequiredService<BusSweep>().Should().BeSameAs(sweep);
    }
}
