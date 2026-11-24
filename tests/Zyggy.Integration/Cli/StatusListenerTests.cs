using System.Globalization;

using Zyggy.Cli.Commands;
using Zyggy.Core.Bus;
using Zyggy.Core.Health;
using Zyggy.Core.Processes;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Cli;

/// <summary>
/// Spec 61 slice 7, AC-22: <c>zyggy status</c> through the verb names the listener that holds the bus checkout from its owner record —
/// one case per line: the listener line, the stray line with the remedy (amber), the version-differs line (amber), the stale-record line
/// (information only). The live holder is this test process (its real pid and start time); the node's <c>/health</c> is a green stand-in.
/// </summary>
public sealed class StatusListenerTests : IAsyncLifetime
{
    private static readonly string Cli = BusHost.InformationalVersion();

    private readonly ProcessFacts _self = OsProcessTable.Instance.Self();

    private CliHarness _harness = null!;

    private StubHealthServer _health = null!;

    private DateTimeOffset _started;

    private string Pid => _self.Pid.ToString(CultureInfo.InvariantCulture);

    public async ValueTask InitializeAsync()
    {
        _harness = await CliHarness.CreateAsync(CancellationToken.None);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        _started = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, TimeSpan.Zero).AddMinutes(-30);
        var green = new NodeHealth(Cli, "acme", "home-laptop", now, null, now, "0123456789ab", 0, 0, null, 4999, null, true, null, _self.Pid, _started);
        _health = new StubHealthServer(NodeHealthJson.Serialize(green));
    }

    public async ValueTask DisposeAsync()
    {
        await _health.DisposeAsync();
        await _harness.DisposeAsync();
    }

    [Fact]
    public async Task Status_LiveListener_ListenerLineGreen()
    {
        // Arrange
        Owner(Cli);

        // Act
        var (exit, console) = await StatusAsync();

        // Assert
        exit.Should().Be(0, console.Stdout + console.Stderr);
        console.Stdout.Should().Contain($"\n        listener pid {Pid} since {Stamp(_started)}\n");
    }

    [Fact]
    public async Task Status_StartRefusedAfterHolderStarted_StrayLineWithRemedyAmber()
    {
        // Arrange
        Owner(Cli);
        DateTimeOffset refused = _started.AddMinutes(20);
        ListenerOwner.WriteRefused(_harness.Bus.CloneDir, new RefusedStart(refused, _self.Pid));

        // Act
        var (exit, console) = await StatusAsync();

        // Assert
        exit.Should().Be(1, console.Stdout + console.Stderr);
        console.Stdout.Should().StartWith("node    home-laptop (acme) · zyggy-node " + Cli + " · zyggy-core unknown · health: amber — stray listener")
            .And.Contain($"\n        stray listener: pid {Pid} since {Stamp(_started)} (zyggy-node {Cli}) holds the bus checkout; a start was refused at {Stamp(refused)}. Stop it with: zyggy node stop\n");
    }

    [Fact]
    public async Task Status_ListenerOtherVersion_RestartLineAmber()
    {
        // Arrange
        Owner("0.30.0");

        // Act
        var (exit, console) = await StatusAsync();

        // Assert
        exit.Should().Be(1, console.Stdout + console.Stderr);
        console.Stdout.Should().Contain("health: amber — listener runs 0.30.0, installed " + Cli)
            .And.Contain($"\n        listener runs 0.30.0, installed {Cli}: restart the node\n");
    }

    [Fact]
    public async Task Status_RecordOfAProcessThatEnded_StaleLineStillGreen()
    {
        // Arrange: a record naming a pid with another start time than any process running under it
        ListenerOwner.WriteOwner(_harness.Bus.CloneDir, new ListenerRecord(_self.Pid, _self.StartedUtc!.Value.AddDays(-3), Cli, _started));
        ListenerOwner.WriteRefused(_harness.Bus.CloneDir, new RefusedStart(_started.AddMinutes(20), _self.Pid));

        // Act
        var (exit, console) = await StatusAsync();

        // Assert
        exit.Should().Be(0, console.Stdout + console.Stderr);
        console.Stdout.Should().Contain($"\n        listener record names pid {Pid}, which is not running\n").And.NotContain("stray listener");
    }

    [Fact]
    public async Task Status_NoRecord_NoListenerLine()
    {
        // Act: guard — a checkout no listener of 0.30.1 ever held prints no listener line
        var (exit, console) = await StatusAsync();

        // Assert
        exit.Should().Be(0, console.Stdout + console.Stderr);
        console.Stdout.Should().NotContain("listener");
    }

    private static string Stamp(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private void Owner(string version) =>
        ListenerOwner.WriteOwner(_harness.Bus.CloneDir, new ListenerRecord(_self.Pid, _self.StartedUtc!.Value, version, _started));

    private Task<(int Exit, Core.Tests.Infrastructure.VerbConsole Console)> StatusAsync() =>
        _harness.RunAsync(["status"], _harness.HomeLaptopNodeJson, extraEnvironment: new Dictionary<string, string?> { ["ZYGGY_HEALTH_URL"] = _health.Url.ToString() }, ct: TestContext.Current.CancellationToken);
}
