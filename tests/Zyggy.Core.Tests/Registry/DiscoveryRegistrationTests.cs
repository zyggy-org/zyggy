using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Zyggy.Core.Configuration;
using Zyggy.Core.Memory;
using Zyggy.Core.Registry;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Registry;

/// <summary>Plan 14 Step 4: <c>AddNodeServices</c> registers the discovery scanner with the options of <c>node.json</c>.</summary>
public sealed class DiscoveryRegistrationTests : IDisposable
{
    private static readonly string PatternsPath = Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt");

    private readonly ScanTree _tree = new();

    public void Dispose() => _tree.Dispose();

    private static NodeConfiguration Configuration(DiscoverySettings? discovery = null) =>
        new(TenantId.Parse("acme"), MachineName.Parse("home-laptop"), TestKeys.Acme1,
            new BusSettings("acme-org", "acme-bus", "main", "/bus", "https://example.invalid/acme-org/acme-bus.git"),
            new PollSettings(), new SecretsSettings(SecretStoreKind.File, "/secrets"))
        {
            Discovery = discovery ?? new DiscoverySettings(),
        };

    private string ProjectWithAgent()
    {
        string root = _tree.Root("src");
        string project = _tree.Project(root, "calizr", git: false);
        _tree.File(project, ".claude/agents/env-debugger.md", "---\ndescription: Diagnoses failing environments\n---\n");
        return root;
    }

    [Fact]
    public void AddNodeServices_ResolvesScannerWithOptionsFromNodeJson()
    {
        // Arrange
        using var provider = new ServiceCollection()
            .AddNodeServices(Configuration(new DiscoverySettings(15, 24, DiscoveryDetail.Names)))
            .BuildServiceProvider();

        // Act
        DiscoveryOptions options = provider.GetRequiredService<IOptions<DiscoveryOptions>>().Value;

        // Assert
        provider.GetRequiredService<DiscoveryScanner>().Should().NotBeNull();
        options.IntervalMinutes.Should().Be(15);
        options.LastSeenHours.Should().Be(24);
        options.Detail.Should().Be(DiscoveryDetail.Names);
        options.IntervalOverride.Should().BeNull();
    }

    [Fact]
    public async Task AddNodeServices_WithSecretPatternsRegistered_ScannerUsesThem()
    {
        // Arrange
        string root = ProjectWithAgent();
        IServiceCollection services = new ServiceCollection().AddNodeServices(Configuration());
        services.AddSecretPatterns(new Dictionary<string, string?>(StringComparer.Ordinal) { ["ZYGGY_SECRET_PATTERNS"] = PatternsPath }).Should().BeNull();
        using var provider = services.BuildServiceProvider();

        // Act
        ScanResult result = await provider.GetRequiredService<DiscoveryScanner>()
            .ScanAsync([root], DiscoveryDetail.Full, TestContext.Current.CancellationToken);

        // Assert
        result.EffectiveDetail.Should().Be(DiscoveryDetail.Full);
        result.Projects.Single().Agents.Should().Equal(new RegistryEntry("env-debugger", "Diagnoses failing environments"));
    }

    [Fact]
    public async Task AddNodeServices_WithoutPatterns_ScanFallsBackToNames()
    {
        // Arrange
        string root = ProjectWithAgent();
        using var provider = new ServiceCollection().AddNodeServices(Configuration()).BuildServiceProvider();

        // Act
        ScanResult result = await provider.GetRequiredService<DiscoveryScanner>()
            .ScanAsync([root], DiscoveryDetail.Full, TestContext.Current.CancellationToken);

        // Assert
        result.EffectiveDetail.Should().Be(DiscoveryDetail.Names);
        result.Projects.Single().Agents.Should().BeEmpty();
        result.Notices.Should().ContainSingle().Which.Kind.Should().Be(ScanNoticeKind.PatternsUnavailable);
    }
}
