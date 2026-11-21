using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;
using Zyggy.Core.Registry;
using Zyggy.Integration.Cli;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Jobs;

/// <summary>
/// Spec 18 slice 14 (AC-31): the node service never runs a session task. Central submits one under a mandate with <c>sessions: true</c>;
/// <c>home-laptop</c>'s production listener leaves it in <c>jobs/</c> — not claimed, not rejected, not reported — and
/// <c>tools/fake-claude</c> never starts.
/// </summary>
public sealed class SessionTaskListenerTests : IAsyncLifetime
{
    private const string Project = "shop";

    private CliHarness _harness = null!;

    private WorktreeProject _project = null!;

    private string CapturePath => _harness.Scratch.File("capture.bin");

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

    // AC-31
    [Fact]
    public async Task Listener_SessionTask_LeftQueued_ModelNeverStarted()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using ServiceProvider laptop = await LaptopAsync(ct);
        string mandate = await GrantAsync(ct);
        var (submitExit, submit) = await _harness.RunAsync(["submit", "--to", "home-laptop", "--mandate", mandate, "--kind", "build", "--step", "slice 1", "--session"], _harness.CentralNodeJson, ct: ct);
        submitExit.Should().Be(0, submit.Stderr);
        (await laptop.GetRequiredService<IBusProvider>().PullAsync(ct)).Succeeded.Should().BeTrue();

        // Act
        int handled = await laptop.GetRequiredService<BusListener>().ProcessOnceAsync(ct);

        // Assert
        new
        {
            Handled = handled,
            Queued = (await FilesOnMainAsync(BusPaths.Jobs(CliHarness.Acme, CliHarness.HomeLaptop), ct)).Count,
            Claimed = (await FilesOnMainAsync(BusPaths.Claimed(CliHarness.Acme, CliHarness.HomeLaptop), ct)).Count,
            Reports = (await FilesOnMainAsync(BusPaths.Reports(CliHarness.Acme, CliHarness.HomeLaptop), ct)).Count,
            ModelRan = File.Exists(CapturePath),
        }.Should().BeEquivalentTo(new { Handled = 0, Queued = 1, Claimed = 0, Reports = 0, ModelRan = false });
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
            o.NodeDir = _project.NodeDir;
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

    private async Task<string> GrantAsync(CancellationToken ct)
    {
        string[] args =
        [
            "mandate", "grant", "--to", "home-laptop", "--project", Project, "--goal", "Build the shop", "--spec", "_specs/12-shop.md", "--spec-commit", "0123456789abcdef0123456789abcdef01234567",
            "--allowed-tools", "Read,Edit", "--max-jobs", "3", "--expires", "3d", "--sessions",
        ];
        var (exit, console) = await _harness.RunAsync(args, _harness.CentralNodeJson, ct: ct);
        exit.Should().Be(0, console.Stderr);
        return console.Stdout.Split(' ', 3)[1].Trim();
    }

    private async Task<IReadOnlyList<string>> FilesOnMainAsync(string folder, CancellationToken ct)
    {
        string listing = await _harness.Bus.RunGitAsync(_harness.Bus.BareDir, ["ls-tree", "-r", "--name-only", "main", "--", folder], ct);
        return [.. listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.EndsWith(".md", StringComparison.Ordinal) && string.Equals(p[..p.LastIndexOf('/')], folder, StringComparison.Ordinal))];
    }
}
