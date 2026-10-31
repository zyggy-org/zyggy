using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;
using Zyggy.Core.Envelope;
using Zyggy.Core.Jobs;
using Zyggy.Core.Memory;
using Zyggy.Core.Models;
using Zyggy.Core.Policy;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Configuration;

/// <summary>Plan 07 Step 8 (ex-Q8): <c>AddNodeServices</c> is the one composition root the Node, the CLI and the tests build from.</summary>
public sealed class NodeServicesRegistrationTests
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    [Fact]
    public void AddNodeServices_ResolvesTheProductionTypes()
    {
        // Arrange
        NodeConfiguration configuration = Configuration();
        using var provider = new ServiceCollection().AddNodeServices(configuration).BuildServiceProvider();

        // Act / Assert
        provider.GetRequiredService<IBusProvider>().Should().BeOfType<GitHubBusProvider>();
        provider.GetRequiredService<ISecretStore>().Should().BeOfType<FileSecretStore>();
        provider.GetRequiredService<IModelRunner>().Should().BeOfType<ClaudeCodeCliRunner>();
        provider.GetRequiredService<IPolicySource>().Should().BeOfType<NodeJsonPolicySource>();
        provider.GetRequiredService<JobHandler>().Method.Name.Should().Be(nameof(JobRunner.RunAsync));
        provider.GetRequiredService<BusListener>().Should().NotBeNull();
        provider.GetRequiredService<NodeConfiguration>().Should().BeSameAs(configuration);
        provider.GetRequiredService<KeyId>().Should().Be(TestKeys.Acme1);
    }

    [Fact]
    public void AddNodeServices_ClaudePath_BindsClaudeCodeOptions()
    {
        // Arrange
        using var provider = new ServiceCollection().AddNodeServices(Configuration() with { Claude = new ClaudeSettings(Path: "/opt/x/claude") }).BuildServiceProvider();

        // Act
        string path = provider.GetRequiredService<IOptions<ClaudeCodeOptions>>().Value.Path;

        // Assert
        path.Should().Be("/opt/x/claude");
    }

    [Fact]
    public void AddNodeServices_ClaudePathNull_KeepsDefault()
    {
        // Arrange
        using var provider = new ServiceCollection().AddNodeServices(Configuration()).BuildServiceProvider();

        // Act
        string path = provider.GetRequiredService<IOptions<ClaudeCodeOptions>>().Value.Path;

        // Assert
        path.Should().Be(new ClaudeCodeOptions().Path);
    }

    [Fact]
    public void AddNodeServices_PollSettings_BecomePollOptionsIncludingSafetyFetch()
    {
        // Arrange
        using var provider = new ServiceCollection().AddNodeServices(Configuration() with { Poll = new PollSettings(3, 7, 2) }).BuildServiceProvider();

        // Act
        PollOptions options = provider.GetRequiredService<PollOptions>();

        // Assert
        options.ActiveInterval.Should().Be(TimeSpan.FromSeconds(3));
        options.IdleInterval.Should().Be(TimeSpan.FromSeconds(7));
        options.SafetyFetchInterval.Should().Be(TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void AddNodeServices_SignWithNull_NoKeyIdRegistered()
    {
        // Arrange
        using var provider = new ServiceCollection().AddNodeServices(Configuration() with { SignWith = null }).BuildServiceProvider();

        // Act
        KeyId? key = provider.GetService<KeyId>();

        // Assert
        key.Should().BeNull();
    }

    [Theory]
    [InlineData("https://github.com/acme-org/acme-bus.git", "https://github.com/acme-org/acme-bus.git", null)]
    [InlineData("file:///C:/bus.git", "file:///C:/bus.git", null)]
    [InlineData("git@github.com-zyggy-bus:acme-org/acme-bus.git", null, "ssh://git@github.com-zyggy-bus/acme-org/acme-bus.git")]
    [InlineData("ssh://git@github.com/acme-org/acme-bus.git", null, "ssh://git@github.com/acme-org/acme-bus.git")]
    public void BusRemoteFrom_HttpsAndSsh_MappedAsBefore(string remote, string? https, string? ssh)
    {
        // Act: moved from the CLI's BusHost (spec 04)
        BusRemote mapped = Zyggy.Core.Configuration.ServiceCollectionExtensions.BusRemoteFrom(new BusSettings("acme-org", "acme-bus", "main", "/bus", remote));

        // Assert
        mapped.Owner.Should().Be("acme-org");
        mapped.Repository.Should().Be("acme-bus");
        mapped.Branch.Should().Be("main");
        mapped.HttpsUrl?.ToString().Should().Be(https);
        mapped.SshUrl?.ToString().Should().Be(ssh);
    }

    [Fact]
    public void AddNodeServices_NoJobGate_ListenerAdmitsAll()
    {
        // Arrange
        using var provider = new ServiceCollection().AddNodeServices(Configuration()).BuildServiceProvider();

        // Act
        JobGate? gate = provider.GetService<JobGate>();

        // Assert: the probe path (04) — no gate registered, the listener admits every job
        gate.Should().BeNull();
    }

    [Fact]
    public void AddNodeServices_ThenAddJobDispatcher_ListenerGetsTheGate()
    {
        // Arrange
        using var provider = new ServiceCollection().AddNodeServices(Configuration()).AddJobDispatcher().BuildServiceProvider();

        // Act
        JobGate gate = provider.GetRequiredService<JobGate>();

        // Assert
        gate.Target.Should().BeOfType<JobDispatcher>();
        provider.GetRequiredService<BusListener>().Should().NotBeNull();
    }

    [Fact]
    public void AddNodeServices_ReturnsSameCollection()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        IServiceCollection returned = services.AddNodeServices(Configuration());

        // Assert
        returned.Should().BeSameAs(services);
    }

    private static NodeConfiguration Configuration() =>
        new(Acme, HomeLaptop, TestKeys.Acme1, new BusSettings("acme-org", "acme-bus", "main", "/bus", "https://example.invalid/acme-org/acme-bus.git"), new PollSettings(), new SecretsSettings(SecretStoreKind.File, "/secrets"));

    // Spec 12 Step 2 (A1, A14): the context submitter resolves only when the key and the secret patterns are registered.
    [Fact]
    public void AddNodeServices_WithKeyAndPatterns_ResolvesContextSubmitter()
    {
        // Arrange
        IServiceCollection services = new ServiceCollection().AddNodeServices(Configuration());
        services.AddSecretPatterns(new Dictionary<string, string?>(StringComparer.Ordinal) { ["ZYGGY_SECRET_PATTERNS"] = PatternsPath }).Should().BeNull();
        using var provider = services.BuildServiceProvider();

        // Act / Assert
        provider.GetRequiredService<ContextSubmitter>().Should().NotBeNull();
    }

    [Fact]
    public void AddNodeServices_WithoutPatterns_ResolvingContextSubmitterThrows()
    {
        // Arrange
        using var provider = new ServiceCollection().AddNodeServices(Configuration()).BuildServiceProvider();

        // Act
        Action act = () => provider.GetRequiredService<ContextSubmitter>();

        // Assert
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void AddNodeServices_RegistersContextEnvelopeOptionsDefaults()
    {
        // Arrange
        using var provider = new ServiceCollection().AddNodeServices(Configuration()).BuildServiceProvider();

        // Act
        var options = provider.GetRequiredService<ContextEnvelopeOptions>();

        // Assert
        options.Should().Be(new ContextEnvelopeOptions());
        options.MaxFactChars.Should().Be(1000);
        options.MaxHookLinesPerSession.Should().Be(150);
        options.StaleSpoolAge.Should().Be(TimeSpan.FromMinutes(60));
        options.MaxBodyBytes.Should().Be(65_536);
    }

    [Fact]
    public void AddNodeServices_ResolvesContextSpoolUnderEffectiveNodeDir()
    {
        // Arrange
        NodeConfiguration configuration = Configuration() with { NodeDir = "/node" };
        using var provider = new ServiceCollection().AddNodeServices(configuration).BuildServiceProvider();

        // Act
        var spool = provider.GetRequiredService<ContextSpool>();

        // Assert
        spool.Directory.Should().Be(Path.GetFullPath(ContextSpool.DirectoryFor("/node", Acme)));
    }

    [Fact]
    public void AddNodeServices_WithPatterns_ResolvesContextStopHook()
    {
        // Arrange
        IServiceCollection services = new ServiceCollection().AddNodeServices(Configuration());
        services.AddSecretPatterns(new Dictionary<string, string?>(StringComparer.Ordinal) { ["ZYGGY_SECRET_PATTERNS"] = PatternsPath }).Should().BeNull();
        using var provider = services.BuildServiceProvider();

        // Act / Assert
        provider.GetRequiredService<ContextStopHook>().Should().NotBeNull();
        Action withoutPatterns = () => new ServiceCollection().AddNodeServices(Configuration()).BuildServiceProvider().GetRequiredService<ContextStopHook>();
        withoutPatterns.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void AddNodeServices_WithKeyAndPatterns_ResolvesContextFlusher()
    {
        // Arrange
        IServiceCollection services = new ServiceCollection().AddNodeServices(Configuration());
        services.AddSecretPatterns(new Dictionary<string, string?>(StringComparer.Ordinal) { ["ZYGGY_SECRET_PATTERNS"] = PatternsPath }).Should().BeNull();
        using var provider = services.BuildServiceProvider();

        // Act / Assert
        provider.GetRequiredService<ContextFlusher>().Should().NotBeNull();
    }

    private static string PatternsPath => Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt");
}
