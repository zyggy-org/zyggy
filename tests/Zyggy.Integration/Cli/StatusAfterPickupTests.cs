using System.Net;
using System.Net.Sockets;

using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Cli;

/// <summary>
/// Spec 13 AC-25: once Central's pickup archived a report within seconds, <c>zyggy status</c>, <c>zyggy status --id</c> and the probe's
/// report search on Central still find it (the readers also read <c>archive/YYYY-MM/</c>).
/// </summary>
public sealed class StatusAfterPickupTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Created = new(2026, 10, 29, 10, 42, 7, TimeSpan.Zero);

    private const string Body = "Root cause is the expired staging certificate\n";

    private readonly PickupMemory _memory = new();

    private CliHarness _harness = null!;

    private ServiceProvider _services = null!;

    private string _centralNodeJson = null!;

    private Dictionary<string, string?> _closedHealth = null!;

    private EnvelopeId _job = null!;

    private SeededJob _report = null!;

    public async ValueTask InitializeAsync()
    {
        var ct = CancellationToken.None;
        _harness = await CliHarness.CreateAsync(ct);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        _closedHealth = new() { ["ZYGGY_HEALTH_URL"] = $"http://127.0.0.1:{port}/health" };
        _centralNodeJson = _harness.WriteNodeJson(CliHarness.Central, _harness.Bus.CloneDir, "central-clone");

        // a job from central to home-laptop and its done report, then one pickup pass on central archives the report
        SeededJob job = await _harness.Bus.SeedSignedJobAsync(CliHarness.Central, CliHarness.HomeLaptop, TestKeys.Acme1, _harness.Store, "probe\n", ct: ct);
        _job = job.Id;
        _report = await _harness.Bus.SeedSignedReportAsync(CliHarness.HomeLaptop, CliHarness.Central, _job, TestKeys.Acme1, _harness.Store, Body, created: Created, ct: ct);
        _services = TestHost.Build(_harness.Bus, CliHarness.Central, _harness.Store, extend: s => s.AddBusPickup(_memory.Configuration));
        await _harness.Bus.PullCloneAsync(ct);
        PickupResult result = await _services.GetRequiredService<BusPickup>().RunAsync(ct);
        result.Archive.Should().Be(new Pushed(1));
        (await _harness.Bus.ExistsOnMainAsync(_report.RelativePath, ct)).Should().BeFalse();
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _harness.DisposeAsync();
        _memory.Dispose();
    }

    [Fact]
    public async Task StatusId_AfterPickup_ReportedByHomeLaptopWithBody()
    {
        // Act
        var (exit, console) = await _harness.RunAsync(["status", "--id", _job.Value], _centralNodeJson, extraEnvironment: _closedHealth, ct: TestContext.Current.CancellationToken);

        // Assert
        exit.Should().Be(0, console.Stderr);
        string[] lines = console.Stdout.Split('\n');
        lines[0].Should().StartWith($"job     {_job.Value}  ");
        lines[1].Should().StartWith("state   reported by home-laptop");
        lines.Should().Contain(l => l.StartsWith($"report  {_report.Id.Value}  done", StringComparison.Ordinal));
        console.Stdout.Should().EndWith("body:\n" + Body);
    }

    [Fact]
    public async Task Status_AfterPickup_RecentReportsListsTheArchivedReport_LiveReportsColumnZero()
    {
        // Act
        var (exit, console) = await _harness.RunAsync(["status"], _centralNodeJson, extraEnvironment: _closedHealth, ct: TestContext.Current.CancellationToken);

        // Assert
        exit.Should().Be(6, console.Stderr);
        string[] lines = console.Stdout.Split('\n');
        lines[4].Should().Be("        home-laptop        1        0         0        0        0", "the job is still queued; the report left reports/");
        lines[5].Should().Be("recent reports (1 of 1)");
        lines[6].Should().StartWith($"        {_report.Id.Value}  home-laptop→central  done  in_reply_to {_job.Value}  ");
    }

    [Fact]
    public async Task FindReportsAsync_AfterPickup_ProbeStillFindsItsAnswer()
    {
        // Act
        IReadOnlyList<Model> reports = await _services.GetRequiredService<BusRepository>().FindReportsAsync(CliHarness.HomeLaptop, _job, TestContext.Current.CancellationToken);

        // Assert
        reports.Should().ContainSingle().Which.Header.Id.Should().Be(_report.Id);
    }
}
