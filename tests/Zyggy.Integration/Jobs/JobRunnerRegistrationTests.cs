using System.Text;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;
using Zyggy.Core.Envelope;
using Zyggy.Core.Jobs;
using Zyggy.Core.Models;
using Zyggy.Core.Policy;
using Zyggy.Core.Processes;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Jobs;

/// <summary>Spec 06 AC-25: the production composition <c>AddProcessRunner + AddClaudeCodeModelRunner + AddNodeJsonPolicy + AddJobRunner</c>.</summary>
public sealed class JobRunnerRegistrationTests : IAsyncLifetime
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
    public async Task AddJobRunner_WithModelPolicyAndProcessRunner_ResolvesJobRunnerAndJobHandler()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using ServiceProvider services = Composition().BuildServiceProvider();
        Model job = Model.CreateJob(
            new EnvelopeHeader(Acme, EnvelopeId.New(DateTimeOffset.UtcNow), Central, HomeLaptop, DateTimeOffset.UtcNow),
            new JobFields("calizr", TimeoutMinutes: 2),
            Encoding.UTF8.GetBytes("probe\n"));

        // Act
        services.GetRequiredService<JobRunner>().Should().NotBeNull();
        JobReply reply = await services.GetRequiredService<JobHandler>()(job, ct);

        // Assert
        reply.Status.Should().Be(ReportStatus.Rejected);
        reply.Reason.Should().Be("unknown_project");
    }

    [Fact]
    public void AddJobRunner_WithoutIModelRunner_ResolvingThrowsInvalidOperationException()
    {
        // Arrange
        using ServiceProvider services = new ServiceCollection()
            .AddProcessRunner()
            .AddSingleton(Configuration())
            .AddNodeJsonPolicy()
            .AddJobRunner()
            .BuildServiceProvider();

        // Act
        Action resolve = () => services.GetRequiredService<JobRunner>();

        // Assert
        resolve.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void AddJobRunner_WithoutIPolicySource_ResolvingThrowsInvalidOperationException()
    {
        // Arrange
        using ServiceProvider services = new ServiceCollection()
            .AddProcessRunner()
            .AddClaudeCodeModelRunner(o => o.Path = FakeClaude.ExecutablePath)
            .AddJobRunner()
            .BuildServiceProvider();

        // Act
        Action resolve = () => services.GetRequiredService<JobRunner>();

        // Assert
        resolve.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddJobRunner_BeforeOrAfterAddGitHubBus_BusCommitsKeepTheirIdentity(bool jobRunnerAfterBus)
    {
        // Arrange: a job naming a missing project is answered without the model; the report commit carries the bus's identity.
        var ct = TestContext.Current.CancellationToken;
        await using ServiceProvider services = TestHost.BuildRunnerListener(
            _bus, HomeLaptop, _store, FakeClaude.ExecutablePath, TestHost.NodeConfigurationFor(_bus, HomeLaptop), jobRunnerAfterBus: jobRunnerAfterBus);
        BusListener listener = services.GetRequiredService<BusListener>();
        (await listener.StartAsync(ct)).Should().Be(BusStartupResult.Clean);
        await _bus.SeedSignedJobAsync(Central, HomeLaptop, TestKeys.Acme1, _store, "probe\n", fields: new JobFields("calizr", TimeoutMinutes: 2), ct: ct);
        (await services.GetRequiredService<IBusProvider>().PullAsync(ct)).Succeeded.Should().BeTrue();

        // Act
        (await listener.ProcessOnceAsync(ct)).Should().Be(1);

        // Assert
        (await _bus.LastCommitSubjectAsync(ct)).Should().StartWith("acme report ");
        (await _bus.RunGitAsync(_bus.BareDir, ["log", "-1", "--format=%an <%ae>", "main"], ct)).Should().Be("zyggy (home-laptop) <home-laptop@zyggy.org>");
    }

    [Fact]
    public void AddJobRunner_NodeDirFromRegisteredNodeConfiguration_WhenNotConfigured()
    {
        // Arrange
        string nodeDir = Path.Combine(Path.GetTempPath(), "zyggy-it", "node-dir-from-config");
        string configured = Path.Combine(Path.GetTempPath(), "zyggy-it", "node-dir-configured");
        using ServiceProvider fromConfig = Composition(Configuration() with { NodeDir = nodeDir }).BuildServiceProvider();
        using ServiceProvider explicitly = Composition(Configuration() with { NodeDir = nodeDir }, o => o.NodeDir = configured).BuildServiceProvider();

        // Act
        string? fromConfigDir = fromConfig.GetRequiredService<IOptions<JobRunnerOptions>>().Value.NodeDir;
        string? explicitDir = explicitly.GetRequiredService<IOptions<JobRunnerOptions>>().Value.NodeDir;

        // Assert
        fromConfigDir.Should().Be(nodeDir);
        explicitDir.Should().Be(configured);
    }

    private NodeConfiguration Configuration() => TestHost.NodeConfigurationFor(_bus, HomeLaptop);

    private IServiceCollection Composition(NodeConfiguration? configuration = null, Action<JobRunnerOptions>? runner = null) =>
        new ServiceCollection()
            .AddProcessRunner()
            .AddSingleton(configuration ?? Configuration())
            .AddClaudeCodeModelRunner(o => o.Path = FakeClaude.ExecutablePath)
            .AddNodeJsonPolicy()
            .AddJobRunner(runner);
}
