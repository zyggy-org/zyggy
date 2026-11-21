using System.Net;
using System.Net.Sockets;

using Zyggy.Core.Bus;
using Zyggy.Core.Health;
using Zyggy.Core.Registry;
using Zyggy.Core.Tenancy;

namespace Zyggy.Integration.Cli;

/// <summary>
/// Spec 59 AC-10 (CLI side): <c>zyggy status</c> lists each machine's <c>last_seen</c>, the node's <c>version</c> and <c>health</c> from
/// its registry file, and says <c>stale</c> only once <c>last_seen</c> is older than <c>discovery.lastSeenHours</c>.
/// </summary>
public sealed class StatusRegistryTests : IAsyncLifetime
{
    private CliHarness _harness = null!;

    private int _closedPort;

    private Dictionary<string, string?> ClosedHealth => new() { ["ZYGGY_HEALTH_URL"] = $"http://127.0.0.1:{_closedPort}/health" };

    public async ValueTask InitializeAsync()
    {
        _harness = await CliHarness.CreateAsync(CancellationToken.None);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _closedPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
    }

    public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

    [Fact]
    public async Task Status_RegistryFiles_OneLinePerMachineWithLastSeenVersionHealth_StaleOnlyPastTheThreshold()
    {
        // Arrange: central seen 5 minutes ago on 0.26.0; home-laptop seen 4 hours ago on 0.25.0 (fresh: the threshold is 6 h);
        // work-laptop seen 7 hours ago, a file from before spec 59 (no version)
        var ct = TestContext.Current.CancellationToken;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset central = Seconds(now.AddMinutes(-5));
        DateTimeOffset home = Seconds(now.AddHours(-4));
        DateTimeOffset work = Seconds(now.AddHours(-7));
        await SeedAsync(new RegistryDocument(CliHarness.Acme, CliHarness.Central, RegistryOs.Linux, central, HealthLevel.Green, null, []) { Version = "0.26.0" }, ct);
        await SeedAsync(new RegistryDocument(CliHarness.Acme, CliHarness.HomeLaptop, RegistryOs.Windows, home, HealthLevel.Amber, null, []) { Version = "0.25.0" }, ct);
        await SeedAsync(new RegistryDocument(CliHarness.Acme, MachineName.Parse("work-laptop"), RegistryOs.Windows, work, HealthLevel.Green, null, []), ct);

        // Act
        var (exit, console) = await _harness.RunAsync(["status"], _harness.HomeLaptopNodeJson, extraEnvironment: ClosedHealth, ct: ct);

        // Assert
        exit.Should().Be(6, console.Stderr);
        string[] registry = [.. console.Stdout.Split('\n').Where(l => l.StartsWith("registry ", StringComparison.Ordinal))];
        registry.Should().HaveCount(3);
        registry[0].Should().MatchRegex(@"^registry central       last_seen " + Stamp(central) + @" \(.+ ago\) · version 0\.26\.0 · health green$");
        registry[1].Should().MatchRegex(@"^registry home-laptop   last_seen " + Stamp(home) + @" \(.+ ago\) · version 0\.25\.0 · health amber$");
        registry[2].Should().MatchRegex(@"^registry work-laptop   last_seen " + Stamp(work) + @" \(.+ ago\) · version unknown · health green · stale$");
    }

    [Fact]
    public async Task Status_NoRegistryFile_NoRegistryLine()
    {
        // Act
        var ct = TestContext.Current.CancellationToken;
        var (exit, console) = await _harness.RunAsync(["status"], _harness.HomeLaptopNodeJson, extraEnvironment: ClosedHealth, ct: ct);

        // Assert
        exit.Should().Be(6, console.Stderr);
        console.Stdout.Split('\n').Should().NotContain(l => l.StartsWith("registry ", StringComparison.Ordinal));
    }

    private async Task SeedAsync(RegistryDocument document, CancellationToken ct)
    {
        await _harness.Bus.SeedRawFileAsync(BusPaths.Registry(CliHarness.Acme, document.Machine), document.Serialize(), "acme registry " + document.Machine.Value, ct);
        await _harness.Bus.PullCloneAsync(ct);
    }

    private static DateTimeOffset Seconds(DateTimeOffset at) => new(at.Year, at.Month, at.Day, at.Hour, at.Minute, at.Second, TimeSpan.Zero);

    private static string Stamp(DateTimeOffset at) => System.Text.RegularExpressions.Regex.Escape(at.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture));
}
