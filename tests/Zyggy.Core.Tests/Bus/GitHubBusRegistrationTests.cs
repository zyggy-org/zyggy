using System.Net;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Processes;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Bus;

/// <summary>Spec 04 AC-11 and the §9 registration rule: <c>AddGitHubBus</c> has no hidden default.</summary>
public sealed class GitHubBusRegistrationTests
{
    private static readonly BusRemote Remote = new("acme-org", "acme-bus", "main", new Uri("https://example.invalid/acme-org/acme-bus.git"), null);

    private static readonly BusIdentity Me = new(TenantId.Parse("acme"), MachineName.Parse("home-laptop"));

    [Fact]
    public void CreateHandler_UsesSystemProxyDefaultCredentialsNoRedirect()
    {
        // Act
        using SocketsHttpHandler handler = Zyggy.Core.Bus.ServiceCollectionExtensions.CreateHandler();

        // Assert
        handler.UseProxy.Should().BeTrue();
        handler.DefaultProxyCredentials.Should().BeSameAs(CredentialCache.DefaultCredentials);
        handler.AllowAutoRedirect.Should().BeFalse();
    }

    [Fact]
    public void AddGitHubBus_WithExplicitOptionsAndStore_ResolvesGitHubBusProviderSingleton()
    {
        // Arrange
        using ServiceProvider provider = new ServiceCollection()
            .AddSingleton<ISecretStore>(new InMemorySecretStore())
            .AddSingleton<IProcessRunner>(new RecordingProcessRunner())
            .AddSingleton<TimeProvider>(new FakeTimeProvider())
            .AddGitHubBus(o =>
            {
                o.Remote = Remote;
                o.Identity = Me;
                o.CheckoutRoot = "/bus";
                o.Version = "0.0.1-test";
            })
            .BuildServiceProvider();

        // Act
        IBusProvider bus = provider.GetRequiredService<IBusProvider>();

        // Assert
        bus.Should().BeOfType<GitHubBusProvider>();
        bus.Should().BeSameAs(provider.GetRequiredService<IBusProvider>());
        bus.Remote.Should().Be(Remote);
    }

    [Fact]
    public void AddGitHubBus_WithoutSecretStore_ResolvingThrowsInvalidOperationException()
    {
        // Arrange
        using ServiceProvider provider = new ServiceCollection()
            .AddSingleton<IProcessRunner>(new RecordingProcessRunner())
            .AddSingleton<TimeProvider>(new FakeTimeProvider())
            .AddGitHubBus(o =>
            {
                o.Remote = Remote;
                o.Identity = Me;
                o.CheckoutRoot = "/bus";
            })
            .BuildServiceProvider();
        Action act = () => provider.GetRequiredService<IBusProvider>();

        // Act & Assert
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void AddGitHubBus_WithoutRemote_ResolvingThrowsInvalidOperationException()
    {
        // Arrange: no hidden default for the remote, identity or checkout
        using ServiceProvider provider = new ServiceCollection()
            .AddSingleton<ISecretStore>(new InMemorySecretStore())
            .AddSingleton<IProcessRunner>(new RecordingProcessRunner())
            .AddSingleton<TimeProvider>(new FakeTimeProvider())
            .AddGitHubBus()
            .BuildServiceProvider();
        Action act = () => provider.GetRequiredService<IBusProvider>();

        // Act & Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*Remote*");
    }

    [Fact]
    public void AddGitHubBus_WithKeyId_ResolvesJobSubmitter()
    {
        // Arrange: plan 08 Step 2 — the sender side resolves from the same registration as the listener
        using ServiceProvider provider = new ServiceCollection()
            .AddSingleton<ISecretStore>(new InMemorySecretStore())
            .AddSingleton<IProcessRunner>(new RecordingProcessRunner())
            .AddSingleton<TimeProvider>(new FakeTimeProvider())
            .AddEnvelopeSigning()
            .AddSingleton(TestKeys.Acme1)
            .AddGitHubBus(o =>
            {
                o.Remote = Remote;
                o.Identity = Me;
                o.CheckoutRoot = "/bus";
            })
            .BuildServiceProvider();

        // Act
        JobSubmitter first = provider.GetRequiredService<JobSubmitter>();

        // Assert
        first.Should().BeSameAs(provider.GetRequiredService<JobSubmitter>());
    }

    [Fact]
    public void AddGitHubBus_WithoutKeyId_ResolvingJobSubmitterThrowsInvalidOperationException()
    {
        // Arrange: the verb checks signWith first, as probe send does; the container has no hidden key
        using ServiceProvider provider = new ServiceCollection()
            .AddSingleton<ISecretStore>(new InMemorySecretStore())
            .AddSingleton<IProcessRunner>(new RecordingProcessRunner())
            .AddSingleton<TimeProvider>(new FakeTimeProvider())
            .AddEnvelopeSigning()
            .AddGitHubBus(o =>
            {
                o.Remote = Remote;
                o.Identity = Me;
                o.CheckoutRoot = "/bus";
            })
            .BuildServiceProvider();
        Action act = () => provider.GetRequiredService<JobSubmitter>();

        // Act & Assert
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void AddGitHubBus_ReturnsSameCollection()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        IServiceCollection returned = services.AddGitHubBus();

        // Assert
        returned.Should().BeSameAs(services);
    }
}
