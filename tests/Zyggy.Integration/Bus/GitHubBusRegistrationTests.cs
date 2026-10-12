using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Git;
using Zyggy.Core.Processes;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Bus;

/// <summary>Spec 04 AC-32: the production composition resolves the bus services, and it has no hidden default.</summary>
public sealed class GitHubBusRegistrationTests : IDisposable
{
    private static readonly BusRemote Remote = new("acme-org", "acme-bus", "main", new Uri("https://example.invalid/acme-org/acme-bus.git"), null);

    private readonly ScratchDirectory _scratch = new();

    public void Dispose() => _scratch.Dispose();

    [Fact]
    public void ProductionComposition_ResolvesProviderRepositoryListenerClient()
    {
        // Arrange: the exact chain BusHost builds
        using ServiceProvider services = new ServiceCollection()
            .AddProcessRunner()
            .AddFileSecretStore(o => o.RootDirectory = _scratch.File("secrets"))
            .AddEnvelopeSigning()
            .AddSingleton(TestKeys.Acme1)
            .AddSingleton<JobHandler>((_, _) => Task.FromResult(new JobReply(ReportStatus.Done, null, ReadOnlyMemory<byte>.Empty, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)))
            .AddGitHubBus(o =>
            {
                o.Remote = Remote;
                o.Identity = new BusIdentity(TenantId.Parse("acme"), MachineName.Parse("home-laptop"));
                o.CheckoutRoot = _scratch.File("bus");
                o.Version = "0.0.0-test";
            })
            .BuildServiceProvider();

        // Act
        IBusProvider provider = services.GetRequiredService<IBusProvider>();
        BusRepository repository = services.GetRequiredService<BusRepository>();
        BusListener listener = services.GetRequiredService<BusListener>();
        GitClient git = services.GetRequiredService<GitClient>();

        // Assert
        provider.Should().BeOfType<GitHubBusProvider>().And.BeSameAs(services.GetRequiredService<IBusProvider>());
        repository.Should().BeSameAs(services.GetRequiredService<BusRepository>());
        listener.Should().NotBeNull();
        git.Should().NotBeNull();
        services.GetRequiredService<ISecretStore>().GetType().Name.Should().Be("FileSecretStore");
    }

    [Fact]
    public void AddGitHubBus_WithoutSecretStore_ThrowsOnResolve()
    {
        // Arrange
        using ServiceProvider services = new ServiceCollection()
            .AddProcessRunner()
            .AddEnvelopeSigning()
            .AddGitHubBus(o =>
            {
                o.Remote = Remote;
                o.Identity = new BusIdentity(TenantId.Parse("acme"), MachineName.Parse("home-laptop"));
                o.CheckoutRoot = _scratch.File("bus");
            })
            .BuildServiceProvider();
        Action act = () => services.GetRequiredService<IBusProvider>();

        // Act & Assert
        act.Should().Throw<InvalidOperationException>();
    }
}
