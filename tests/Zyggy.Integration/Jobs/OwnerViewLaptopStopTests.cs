using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;
using Zyggy.Core.Envelope;
using Zyggy.Core.Registry;
using Zyggy.Integration.Cli;
using Zyggy.Integration.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Jobs;

/// <summary>
/// Spec 18 slice 13 (AC-30, laptop side) over one bare repository: <c>zyggy supervise stop</c> on <c>home-laptop</c> writes the marker in
/// its node folder, and its production listener then rejects a job under a mandate with <c>mandate_invalid</c> before any model call;
/// after <c>zyggy supervise start</c> the next one is claimed and run with <c>tools/fake-claude</c>.
/// </summary>
public sealed class OwnerViewLaptopStopTests : IAsyncLifetime
{
    private const string Project = "shop";

    private CliHarness _harness = null!;

    private WorktreeProject _project = null!;

    private string CapturePath => _harness.Scratch.File("capture.bin");

    private static string LaptopReports => BusPaths.Reports(CliHarness.Acme, CliHarness.HomeLaptop);

    public async ValueTask InitializeAsync()
    {
        _harness = await CliHarness.CreateAsync(CancellationToken.None);
        _project = await WorktreeProject.CreateAsync(_harness.Bus, CancellationToken.None);
        var registry = new RegistryDocument(CliHarness.Acme, CliHarness.HomeLaptop, RegistryOs.Windows, DateTimeOffset.UtcNow.AddMinutes(-1), null, null, [])
        {
            Capabilities = [RegistryDocument.SupervisionCapability],
        };
        await _harness.Bus.SeedRawFileAsync(BusPaths.Registry(CliHarness.Acme, CliHarness.HomeLaptop), registry.Serialize(), "seed: home-laptop registry", CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        _project?.Dispose();
        await _harness.DisposeAsync();
    }

    // AC-30
    [Fact]
    public async Task Intake_AfterSuperviseStopOnTheLaptop_RejectedMandateInvalid_ModelNeverStarted()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using ServiceProvider laptop = await LaptopAsync(ct);
        string mandate = await GrantAsync(ct);
        (await _harness.RunAsync(["supervise", "stop"], _harness.HomeLaptopNodeJson, ct: ct)).Exit.Should().Be(0);
        await SubmitAsync(mandate, ct);

        // Act
        await ProcessAsync(laptop, ct);

        // Assert
        Model report = await SingleReportAsync(ct);
        new { report.Report!.Status, report.Report.Reason, ModelRan = File.Exists(CapturePath) }
            .Should().BeEquivalentTo(new { Status = ReportStatus.Rejected, Reason = "mandate_invalid", ModelRan = false });
    }

    // AC-30
    [Fact]
    public async Task Intake_AfterSuperviseStartOnTheLaptop_ClaimedAndRun()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using ServiceProvider laptop = await LaptopAsync(ct);
        string mandate = await GrantAsync(ct);
        (await _harness.RunAsync(["supervise", "stop"], _harness.HomeLaptopNodeJson, ct: ct)).Exit.Should().Be(0);
        (await _harness.RunAsync(["supervise", "start"], _harness.HomeLaptopNodeJson, ct: ct)).Exit.Should().Be(0);
        await SubmitAsync(mandate, ct);

        // Act
        await ProcessAsync(laptop, ct);

        // Assert
        Model report = await SingleReportAsync(ct);
        new { report.Report!.Status, ModelRan = File.Exists(CapturePath) }.Should().BeEquivalentTo(new { Status = ReportStatus.Done, ModelRan = true });
    }

    private async Task<ServiceProvider> LaptopAsync(CancellationToken ct)
    {
        var environment = new Dictionary<string, string>
        {
            ["ZYGGY_FAKE_CLAUDE_SCENARIO"] = "done",
            ["ZYGGY_FAKE_CLAUDE_CAPTURE"] = CapturePath,
        };
        NodeConfiguration configuration = TestHost.NodeConfigurationFor(_harness.Bus, CliHarness.HomeLaptop);
        ServiceProvider services = TestHost.BuildRunnerListener(_harness.Bus, CliHarness.HomeLaptop, _harness.Store, FakeClaude.ExecutablePath, configuration, o =>
        {
            // The node folder `zyggy supervise` writes to: the one HomeLaptopNodeJson names.
            o.NodeDir = _harness.NodeDirOf("home-laptop");
            o.TimeoutOverride = TimeSpan.FromSeconds(30);
            o.ModelEnvironment = environment;
        });
        services.GetRequiredService<RegistrySnapshot>().Replace(new RegistryDocument(
            CliHarness.Acme,
            CliHarness.HomeLaptop,
            RegistryOs.Windows,
            DateTimeOffset.UtcNow,
            null,
            null,
            [new RegistryProject(Project, _project.Path.Replace('\\', '/'), "main", [], [], null)]));
        (await services.GetRequiredService<BusListener>().StartAsync(ct)).Should().Be(BusStartupResult.Clean);
        return services;
    }

    private static async Task ProcessAsync(ServiceProvider laptop, CancellationToken ct)
    {
        (await laptop.GetRequiredService<IBusProvider>().PullAsync(ct)).Succeeded.Should().BeTrue();
        (await laptop.GetRequiredService<BusListener>().ProcessOnceAsync(ct)).Should().Be(1);
    }

    private async Task<string> GrantAsync(CancellationToken ct)
    {
        string[] args =
        [
            "mandate", "grant", "--to", "home-laptop", "--project", Project, "--goal", "Build the shop", "--spec", "_specs/12-shop.md", "--spec-commit", "0123456789abcdef0123456789abcdef01234567",
            "--allowed-tools", "Read,Edit", "--max-jobs", "3", "--expires", "3d",
        ];
        var (exit, console) = await _harness.RunAsync(args, _harness.CentralNodeJson, ct: ct);
        exit.Should().Be(0, console.Stderr);
        return console.Stdout.Split(' ', 3)[1].Trim();
    }

    private async Task SubmitAsync(string mandate, CancellationToken ct)
    {
        var (exit, console) = await _harness.RunAsync(["submit", "--to", "home-laptop", "--mandate", mandate, "--kind", "build", "--step", "slice 1"], _harness.CentralNodeJson, ct: ct);
        exit.Should().Be(0, console.Stderr);
    }

    private async Task<Model> SingleReportAsync(CancellationToken ct)
    {
        string listing = await _harness.Bus.RunGitAsync(_harness.Bus.BareDir, ["ls-tree", "-r", "--name-only", "main", "--", LaptopReports], ct);
        string path = listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.EndsWith(".md", StringComparison.Ordinal))
            .Should().ContainSingle().Subject;
        return await _harness.ParseOnMainAsync(path, ct);
    }
}
