using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Git;
using Zyggy.Core.Registry;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Core.Tests.Registry;

namespace Zyggy.Core.Tests.Bus;

/// <summary>
/// Spec 59 slice 1, AC-5: a sender refuses a <c>fetch</c> job to a machine whose registry file does not advertise the <c>fetch</c>
/// capability (an older node would run it as a model job); nothing reaches the bus. A machine that advertises it gets the job.
/// </summary>
public sealed class JobSubmitterFetchTests : IAsyncLifetime
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName Central = MachineName.Parse("central");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly DateTimeOffset T = new(2026, 11, 21, 10, 0, 0, TimeSpan.Zero);

    private readonly TempCheckout _checkout = new(Acme, Central);

    private readonly RecordingProcessRunner _processes = new();

    private readonly IBusProvider _provider = Substitute.For<IBusProvider>();

    private readonly FakeTimeProvider _clock = new(T);

    private InMemorySecretStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = await TestKeys.StoreWithBothAsync(CancellationToken.None);
        _provider.PullAsync(Arg.Any<CancellationToken>()).Returns(new BusSyncResult(true, string.Empty));
        _provider.PushAsync(Arg.Any<CancellationToken>()).Returns(new PushSucceeded());
    }

    public ValueTask DisposeAsync()
    {
        _checkout.Dispose();
        return ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SubmitAsync_FetchToMachineWithoutFetchCapability_RefusedKindNothingWritten(bool registryPresent)
    {
        // Arrange
        if (registryPresent)
        {
            WriteRegistry(["supervision"]);
        }

        // Act
        SubmitResult result = await Submitter().SubmitAsync(FetchRequest(), TestContext.Current.CancellationToken);

        // Assert
        new { Field = result.Should().BeOfType<SubmitRefused>().Subject.Field, Jobs = _checkout.FilesUnder(BusPaths.Jobs(Acme, HomeLaptop)).Count, Calls = _processes.Calls.Count }
            .Should().BeEquivalentTo(new { Field = "kind", Jobs = 0, Calls = 0 });
    }

    [Fact]
    public async Task SubmitAsync_FetchToMachineAdvertisingFetch_Submitted()
    {
        // Arrange
        WriteRegistry([RegistryDocument.FetchCapability]);

        // Act
        SubmitResult result = await Submitter().SubmitAsync(FetchRequest(), TestContext.Current.CancellationToken);

        // Assert
        result.Should().BeOfType<Submitted>();
    }

    [Fact]
    public async Task SubmitAsync_ModelJobToMachineWithoutRegistry_Submitted()
    {
        // Act
        JobRequest.TryCreate("home-laptop", "shop", null, null, null, null, "Investigate.\n", out JobRequest? request).Should().BeNull();
        SubmitResult result = await Submitter().SubmitAsync(request!, TestContext.Current.CancellationToken);

        // Assert
        result.Should().BeOfType<Submitted>();
    }

    private void WriteRegistry(IReadOnlyList<string> capabilities)
    {
        var document = RegistryDocumentTests.Central() with { Tenant = Acme, Machine = HomeLaptop, Capabilities = capabilities };
        string path = BusPaths.Resolve(_checkout.Root, BusPaths.Registry(Acme, HomeLaptop));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, document.Serialize());
    }

    private JobSubmitter Submitter()
    {
        var git = new GitClient(_processes, new GitClientOptions { Identity = new GitIdentity("zyggy (central)", "central@zyggy.org") }, _clock);
        var signer = new EnvelopeSigner(_store);
        var bus = new BusRepository(git, _provider, new BusIdentity(Acme, Central), _checkout.Root, signer, _clock, () => 0, new RecordingLogger<BusRepository>());
        return new JobSubmitter(bus, signer, TestKeys.Acme1, _clock, new RecordingLogger<JobSubmitter>());
    }

    private static JobRequest FetchRequest()
    {
        JobRequest.TryCreate("home-laptop", "shop", null, null, null, null, "fetch docs/plan.md\n", out JobRequest? request).Should().BeNull();
        request!.TryWithFetch("docs/plan.md", "main", out JobRequest? fetch).Should().BeNull();
        return fetch!;
    }
}
