using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Zyggy.Core.Bus;
using Zyggy.Core.Health;
using Zyggy.Core.Registry;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Cli;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Registry;

/// <summary>Plan 14 Step 6 (AC-22, AC-23): registry files beside the envelopes leave verify and status alone; a push race is retried once.</summary>
public sealed class RegistryPublishRoundTripTests
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static Dictionary<string, string?> NoNode()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return new() { ["ZYGGY_HEALTH_URL"] = $"http://127.0.0.1:{port}/health" };
    }

    [Fact]
    public async Task Verify_RegistryFilesPresent_SkippedVerdictUnchanged()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using CliHarness harness = await CliHarness.CreateAsync(ct);
        string root = harness.Scratch.File("projects");
        Directory.CreateDirectory(root);
        await ScratchProject.CreateInAsync(harness.Bus, root, "calizr", ct);
        string homeJson = harness.WriteNodeJson(CliHarness.HomeLaptop, harness.Bus.CloneDir, "home-registry", devRoots: [root]);
        string centralJson = harness.WriteNodeJson(CliHarness.Central, harness.Bus.SenderDir, "central-registry");
        var (statusBeforeExit, statusBefore) = await harness.RunAsync(["status"], harness.HomeLaptopNodeJson, extraEnvironment: NoNode(), ct: ct);

        // Act
        (await harness.RunAsync(["discover"], homeJson, extraEnvironment: NoNode(), ct: ct)).Exit.Should().Be(0);
        (await harness.RunAsync(["discover"], centralJson, extraEnvironment: NoNode(), ct: ct)).Exit.Should().Be(0);
        await harness.Bus.PullCloneAsync(ct);
        var (verifyExit, verify) = await harness.RunAsync(["verify", "--verbose"], harness.HomeLaptopNodeJson, ct: ct);
        var (statusAfterExit, statusAfter) = await harness.RunAsync(["status"], harness.HomeLaptopNodeJson, extraEnvironment: NoNode(), ct: ct);

        // Assert
        verifyExit.Should().Be(0, verify.Stderr + verify.Stdout);
        verify.Stdout.Split('\n').Where(l => l.StartsWith("SKIP ", StringComparison.Ordinal) && l.EndsWith("registry file (unsigned in v1)", StringComparison.Ordinal))
            .Should().HaveCount(2);
        statusAfterExit.Should().Be(statusBeforeExit);
        static string BusBlock(string stdout) => string.Join('\n', stdout.Split('\n').Skip(2).TakeWhile(l => !l.StartsWith("recent reports", StringComparison.Ordinal)));
        BusBlock(statusAfter.Stdout).Should().Be(BusBlock(statusBefore.Stdout));
        RegistryDocument central = RegistryDocument.Parse(await harness.Bus.ShowBytesAsync(BusPaths.Registry(Acme, CliHarness.Central), ct)).Document!;
        central.Projects.Should().BeEmpty("Central publishes projects: [] without devRoots");
    }

    [Fact]
    public async Task Publish_ForeignCommitBeforePush_RetriedOnceOnTop()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var bus = new BusRepoFixture(Acme);
        await bus.InitializeAsync();
        await bus.SeedTenantAsync(Acme, [CliHarness.Central, HomeLaptop], ct);
        InMemorySecretStore store = await TestHost.StoreAsync(Acme, ct);
        HookedProcessRunner hooks = TestHost.Hooks();
        var log = new RecordingLoggerProvider();
        await using ServiceProvider services = TestHost.Build(bus, HomeLaptop, store, hooks, logging: log, extend: s => s.AddDiscovery());
        bool advanced = false;
        hooks.BeforeRun = async spec =>
        {
            if (!advanced && HookedProcessRunner.SubCommand(spec) == "push")
            {
                advanced = true;
                await bus.AdvanceMainFromSenderAsync(ct);
            }
        };
        var document = new RegistryDocument(Acme, HomeLaptop, RegistryOsExtensions.Current(), null, null, null,
            [new RegistryProject("calizr", "C:/src/calizr", "main", [], [], null)]);

        // Act
        RegistryPublishResult result = await services.GetRequiredService<RegistryPublisher>().PublishAsync(document, HealthLevel.Green, ct);

        // Assert
        result.Outcome.Should().Be(PublishOutcome.Published);
        hooks.Calls.Count(c => HookedProcessRunner.SubCommand(c) == "push").Should().Be(2);
        string[] subjects = (await bus.RunGitAsync(bus.BareDir, ["log", "--format=%s", "-2", "main"], ct)).Split('\n');
        subjects[0].Should().Be("acme registry home-laptop");
        subjects[1].Should().Be("noise 1");
        log.Entries.Count(e => e.Level == LogLevel.Warning && e.Message == "registry: push retried").Should().Be(1);
        SecretSweep.AssertAbsent(hooks.Calls.SelectMany(SecretSweep.TextsOf));
        SecretSweep.AssertAbsent(log.Messages);
    }
}
