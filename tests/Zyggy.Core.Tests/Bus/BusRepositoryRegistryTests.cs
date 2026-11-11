using System.Text;

using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Git;
using Zyggy.Core.Registry;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Core.Tests.Registry;

namespace Zyggy.Core.Tests.Bus;

/// <summary>Spec 14 AC-31: the registry write goes through the one write path, for this machine's own file only.</summary>
public sealed class BusRepositoryRegistryTests : IDisposable
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly BusRemote Remote = new("acme-org", "acme-bus", "main", new Uri("https://example.invalid/acme-org/acme-bus.git"), null);

    private readonly TempCheckout _checkout = new(Acme, HomeLaptop);
    private readonly RecordingProcessRunner _processes = new();
    private readonly IBusProvider _provider = Substitute.For<IBusProvider>();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 7, 9, 0, 0, TimeSpan.Zero));

    public BusRepositoryRegistryTests()
    {
        _provider.Remote.Returns(Remote);
        _provider.PullAsync(Arg.Any<CancellationToken>()).Returns(new BusSyncResult(true, string.Empty));
        _provider.PushAsync(Arg.Any<CancellationToken>()).Returns(new PushSucceeded());
    }

    public void Dispose() => _checkout.Dispose();

    private BusRepository Repository() =>
        new(new GitClient(_processes, new GitClientOptions(), _clock), _provider, new BusIdentity(Acme, HomeLaptop), _checkout.Root,
            new EnvelopeSigner(new Zyggy.Core.Secrets.InMemorySecretStore()), _clock, () => 0, new RecordingLogger<BusRepository>());

    private static RegistryDocument Document() => RegistryDocumentTests.HomeLaptop();

    [Fact]
    public async Task PublishRegistryAsync_WritesBusPathsRegistryWithSubject()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string path = BusPaths.Registry(Acme, HomeLaptop);

        // Act
        WriteOutcome outcome = await Repository().PublishRegistryAsync(Document(), ct);

        // Assert
        outcome.Should().Be(new Pushed(1));
        _checkout.Read(path).Should().Equal(Document().Serialize());
        _processes.CallsOf("add").Single().Arguments.Should().EndWith(path);
        string commitMessage = _processes.CallsOf("commit").Single().StandardInput!;
        commitMessage.Should().Be("acme registry home-laptop");
    }

    [Fact]
    public async Task PublishRegistryAsync_ForeignTenant_Throws()
    {
        // Act
        Func<Task> act = () => Repository().PublishRegistryAsync(Document() with { Tenant = TenantId.Parse("other-org") }, TestContext.Current.CancellationToken);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("write rule: the registry document belongs to another tenant");
        _processes.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task PublishRegistryAsync_ForeignMachine_Throws()
    {
        // Act
        Func<Task> act = () => Repository().PublishRegistryAsync(Document() with { Machine = MachineName.Parse("work-laptop") }, TestContext.Current.CancellationToken);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("write rule: the registry document belongs to another machine");
        _processes.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadOwnRegistryAsync_Absent_Null()
    {
        // Act + Assert
        (await Repository().ReadOwnRegistryAsync(TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task ReadOwnRegistryAsync_NeverReadsAnotherMachinesFile()
    {
        // Arrange
        _checkout.Drop(BusPaths.Registry(Acme, MachineName.Parse("work-laptop")), Encoding.UTF8.GetBytes("schema: 1\n"));

        // Act + Assert
        (await Repository().ReadOwnRegistryAsync(TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task ReadOwnRegistryAsync_Present_Bytes()
    {
        // Arrange
        byte[] bytes = Document().Serialize();
        _checkout.Drop(BusPaths.Registry(Acme, HomeLaptop), bytes);

        // Act + Assert
        (await Repository().ReadOwnRegistryAsync(TestContext.Current.CancellationToken)).Should().Equal(bytes);
    }
}
