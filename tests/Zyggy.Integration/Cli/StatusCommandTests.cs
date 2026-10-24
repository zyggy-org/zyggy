using System.Net;
using System.Net.Sockets;
using System.Text;

using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Cli;

/// <summary>Spec 08 AC-9 and AC-10: <c>zyggy status</c> and <c>zyggy status --id</c> in process against the bare repository, with a closed health port.</summary>
public sealed partial class StatusCommandTests : IAsyncLifetime
{
    private CliHarness _harness = null!;

    private int _closedPort;

    private string HealthUrl => $"http://127.0.0.1:{_closedPort}/health";

    private Dictionary<string, string?> ClosedHealth => new() { ["ZYGGY_HEALTH_URL"] = HealthUrl };

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
    public async Task Status_NoNode_ExitSixFirstLineDownAndBusBlockStillPrinted()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        int commits = await _harness.CommitCountOnMainAsync(ct);

        // Act
        var (exit, console) = await _harness.RunAsync(["status"], _harness.HomeLaptopNodeJson, extraEnvironment: ClosedHealth, ct: ct);

        // Assert
        exit.Should().Be(6, console.Stderr);
        string[] lines = console.Stdout.Split('\n');
        lines[0].Should().Be($"node    home-laptop (acme) · health: down — no node service answering on {HealthUrl}");
        lines[1].Should().StartWith("bus     head ").And.EndWith(" · 1 tenant directory");
        lines[1].Should().MatchRegex(@"^bus     head [0-9a-f]{12} · 1 tenant directory$");
        lines[2].Should().Be("        machine       queued  claimed  rejected  reports  context");
        lines[3].Should().Be("        central            0        0         0        0        0");
        lines[4].Should().Be("        home-laptop        0        0         0        0        0");
        lines[5].Should().Be("recent reports (0 of 0)");
        (await _harness.PorcelainAsync(_harness.Bus.CloneDir, ct)).Should().BeEmpty();
        (await _harness.CommitCountOnMainAsync(ct)).Should().Be(commits);
    }

    [Fact]
    public async Task Status_NonLoopbackHealthUrl_ExitThreeNoRequest()
    {
        // Act
        var ct = TestContext.Current.CancellationToken;
        var started = DateTime.UtcNow;
        var (exit, console) = await _harness.RunAsync(["status"], _harness.HomeLaptopNodeJson, extraEnvironment: new Dictionary<string, string?> { ["ZYGGY_HEALTH_URL"] = "http://example.invalid:4711/health" }, ct: ct);

        // Assert
        exit.Should().Be(3);
        console.Stderr.Should().Be("zyggy: configuration error: health url must be loopback\n");
        console.Stdout.Should().BeEmpty();
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Status_Table_RowAndRecentReports()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Seeded seeded = await SeedTreeAsync(ct);

        // Act
        var (exit, console) = await _harness.RunAsync(["status"], _harness.HomeLaptopNodeJson, extraEnvironment: ClosedHealth, ct: ct);

        // Assert: plan A13 — AC-10's shorthand is the column table of "Output formats"
        exit.Should().Be(6, console.Stderr);
        string[] lines = console.Stdout.Split('\n');
        lines[4].Should().Be("        home-laptop        1        1         1        2        0");
        lines[5].Should().Be("recent reports (2 of 2)");
        lines[6].Should().StartWith($"        {seeded.DoneReport.Value}  home-laptop→central  done  in_reply_to {seeded.DoneJob.Value}  cost_usd 0.0123  ");
        lines[7].Should().StartWith($"        {seeded.RejectedReport.Value}  home-laptop→central  rejected invalid_signature  in_reply_to {seeded.Rejected.Value}  ");
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("claimed")]
    [InlineData("rejected")]
    [InlineData("reported")]
    public async Task StatusId_States_LineAndExit(string which)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Seeded seeded = await SeedTreeAsync(ct);
        EnvelopeId id = which switch { "queued" => seeded.Queued, "claimed" => seeded.Claimed, "rejected" => seeded.Rejected, _ => seeded.DoneJob };

        // Act
        var (exit, console) = await _harness.RunAsync(["status", "--id", id.Value], _harness.HomeLaptopNodeJson, extraEnvironment: ClosedHealth, ct: ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        string[] lines = console.Stdout.Split('\n');
        lines[0].Should().StartWith($"job     {id.Value}  ");
        switch (which)
        {
            case "queued":
                lines[0].Should().Contain("central→home-laptop").And.Contain("project ").And.Contain("priority normal").And.Contain("created ");
                lines[1].Should().MatchRegex(@"^state   queued at home-laptop since \d+ (s|min|h) ago$");
                console.Stdout.Should().NotContain("body:");
                break;
            case "claimed":
                lines[1].Should().MatchRegex(@"^state   claimed by home-laptop since \d+ (s|min|h) ago$");
                break;
            case "rejected":
                lines[0].Should().Be($"job     {id.Value}  (unverified: invalid_signature)");
                lines[1].Should().Be("state   rejected by home-laptop: invalid_signature");
                lines[2].Should().StartWith($"report  {seeded.RejectedReport.Value}  rejected invalid_signature  ");
                lines[3].Should().Be("body:");
                console.Stdout.Should().EndWith("body:\nrejected: invalid_signature\n");
                break;
            default:
                lines[0].Should().Be($"job     {id.Value}  (file gone)");
                lines[1].Should().Be("state   reported by home-laptop");
                lines[2].Should().StartWith($"report  {seeded.DoneReport.Value}  done  ").And.Contain("cost_usd 0.0123");
                lines[3].Should().Be("body:");
                console.Stdout.Should().EndWith("body:\n" + DoneBody);
                break;
        }
    }

    [Fact]
    public async Task StatusId_Unknown_ExitOneNotFound()
    {
        // Act
        var ct = TestContext.Current.CancellationToken;
        var (exit, console) = await _harness.RunAsync(["status", "--id", EnvelopeId.New(DateTimeOffset.UtcNow.AddYears(-1)).Value], _harness.HomeLaptopNodeJson, extraEnvironment: ClosedHealth, ct: ct);

        // Assert
        exit.Should().Be(1);
        console.Stdout.Should().Be("not found in tenant acme\n");
    }

    [Fact]
    public async Task StatusId_NotAUlid_ExitTwo()
    {
        // Act
        var ct = TestContext.Current.CancellationToken;
        var (exit, console) = await _harness.RunAsync(["status", "--id", "not-a-ulid"], _harness.HomeLaptopNodeJson, extraEnvironment: ClosedHealth, ct: ct);

        // Assert
        exit.Should().Be(2);
        console.Stderr.Should().Be("zyggy: --id must be a ULID\n");
    }

    [Fact]
    public async Task StatusId_NeverReadsHealth()
    {
        // Act: a non-loopback URL that would be exit 3 in the plain mode is never parsed with --id
        var ct = TestContext.Current.CancellationToken;
        Seeded seeded = await SeedTreeAsync(ct);
        var (exit, console) = await _harness.RunAsync(["status", "--id", seeded.Queued.Value], _harness.HomeLaptopNodeJson, extraEnvironment: new Dictionary<string, string?> { ["ZYGGY_HEALTH_URL"] = "http://example.invalid:4711/health" }, ct: ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
    }

    [Fact]
    public async Task Status_WritesNothingAndTakesNoListenLock()
    {
        // Arrange: a listener holds the listen lock on the same checkout
        var ct = TestContext.Current.CancellationToken;
        await using ServiceProvider other = TestHost.BuildListener(_harness.Bus, CliHarness.HomeLaptop, _harness.Store);
        using BusListener listener = other.GetRequiredService<BusListener>();
        (await listener.StartAsync(ct)).Should().Be(BusStartupResult.Clean);

        // Act
        var (exit, console) = await _harness.RunAsync(["status"], _harness.HomeLaptopNodeJson, extraEnvironment: ClosedHealth, ct: ct);

        // Assert
        exit.Should().Be(6, console.Stderr);
        console.Stdout.Should().StartWith("node    home-laptop (acme) · health: down");
        (await _harness.PorcelainAsync(_harness.Bus.CloneDir, ct)).Should().BeEmpty();
    }

    private const string DoneBody = "REPORT\nsummary: all good\nfiles_changed: []\n";

    private sealed record Seeded(EnvelopeId Queued, EnvelopeId Claimed, EnvelopeId Rejected, EnvelopeId RejectedReport, EnvelopeId DoneJob, EnvelopeId DoneReport);

    /// <summary>One queued, one claimed, one rejected (tampered) job with its rejection report, and a done report with a cost answering a fourth id — then the node clone pulled.</summary>
    private async Task<Seeded> SeedTreeAsync(CancellationToken ct)
    {
        BusRepoFixture bus = _harness.Bus;
        var store = _harness.Store;
        SeededJob queued = await bus.SeedSignedJobAsync(CliHarness.Central, CliHarness.HomeLaptop, TestKeys.Acme1, store, "probe\n", ct: ct);
        var claimedId = EnvelopeId.New(DateTimeOffset.UtcNow.AddMinutes(-2));
        await bus.SeedSignedJobAsync(CliHarness.Central, CliHarness.HomeLaptop, TestKeys.Acme1, store, "probe\n", fileNameOverride: BusPaths.EnvelopeFile(BusPaths.Claimed(CliHarness.Acme, CliHarness.HomeLaptop), claimedId), ct: ct);
        var rejectedId = EnvelopeId.New(DateTimeOffset.UtcNow.AddMinutes(-3));
        await bus.SeedSignedJobAsync(CliHarness.Central, CliHarness.HomeLaptop, TestKeys.Acme1, store, "probe\n", mutate: FlipSignature, fileNameOverride: BusPaths.EnvelopeFile(BusPaths.Rejected(CliHarness.Acme, CliHarness.HomeLaptop), rejectedId), ct: ct);
        SeededJob rejectedReport = await bus.SeedSignedReportAsync(CliHarness.HomeLaptop, CliHarness.Central, rejectedId, TestKeys.Acme1, store, "rejected: invalid_signature\n", status: ReportStatus.Rejected, reason: "invalid_signature", created: DateTimeOffset.UtcNow.AddMinutes(-1), ct: ct);
        var doneJobId = EnvelopeId.New(DateTimeOffset.UtcNow.AddMinutes(-4));
        SeededJob doneReport = await bus.SeedSignedReportAsync(CliHarness.HomeLaptop, CliHarness.Central, doneJobId, TestKeys.Acme1, store, DoneBody, costUsd: 0.0123m, created: DateTimeOffset.UtcNow, ct: ct);
        await bus.PullCloneAsync(ct);
        return new Seeded(queued.Id, claimedId, rejectedId, rejectedReport.Id, doneJobId, doneReport.Id);
    }

    private static byte[] FlipSignature(byte[] bytes)
    {
        string text = Encoding.UTF8.GetString(bytes);
        int at = text.IndexOf("sig: hmac-sha256:", StringComparison.Ordinal) + "sig: hmac-sha256:".Length;
        return Encoding.UTF8.GetBytes(text[..at] + (text[at] == '0' ? '1' : '0') + text[(at + 1)..]);
    }
}
