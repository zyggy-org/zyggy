using System.Text;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Zyggy.Core.Bus;
using Zyggy.Core.Bus.Probe;
using Zyggy.Core.Envelope;
using Zyggy.Core.Processes;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Bus;

/// <summary>Spec 04 AC-25 (and AC-19/AC-20/AC-22 through the loop): one tick of the real loop on the bare repository.</summary>
public sealed class BusListenerRoundTripTests : IAsyncLifetime
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName Central = MachineName.Parse("central");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private readonly BusRepoFixture _bus = new(Acme);

    private readonly HookedProcessRunner _hooks = TestHost.Hooks();

    private readonly RecordingLoggerProvider _log = new();

    private int _handlerCalls;

    private InMemorySecretStore _store = null!;

    public static TheoryData<string> Cases => new() { "flipped", "to-central", "schema2", "no-front-matter" };

    public async ValueTask InitializeAsync()
    {
        await _bus.InitializeAsync();
        await _bus.SeedTenantAsync(Acme, [Central, HomeLaptop], CancellationToken.None);
        _store = await TestHost.StoreAsync(Acme, CancellationToken.None);
    }

    public ValueTask DisposeAsync() => _bus.DisposeAsync();

    [Fact]
    public async Task RunAsync_JobPushedBySender_OneTickClaimsReportsPushes()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using ServiceProvider services = TestHost.BuildListener(_bus, HomeLaptop, _store, hooks: _hooks, logging: _log);
        BusListener listener = services.GetRequiredService<BusListener>();
        (await listener.StartAsync(ct)).Should().Be(BusStartupResult.Clean);
        SeededJob job = await _bus.SeedSignedJobAsync(Central, HomeLaptop, TestKeys.Acme1, _store, "probe\n", ct: ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Act: run until the report is on main (or 30 s)
        Task run = listener.RunAsync(PollMode.Active, cts.Token);
        string reportsFolder = BusPaths.Reports(Acme, HomeLaptop);
        IReadOnlyList<string> reports = [];
        for (int i = 0; i < 60 && reports.Count == 0; i++)
        {
            await Task.Delay(500, ct);
            reports = await ReportPathsAsync(reportsFolder, ct);
        }

        await Task.Delay(1500, ct); // one more silent tick
        await cts.CancelAsync();
        await run;

        // Assert
        string reportPath = reports.Should().ContainSingle().Subject;
        EnvelopeResult parsed = await new EnvelopeSigner(_store).ParseAndVerifyAsync(await _bus.ShowBytesAsync(reportPath, ct), Acme, ct);
        parsed.IsAccepted.Should().BeTrue(parsed.Rejection?.ToString());
        Model report = parsed.Envelope!;
        report.Header.InReplyTo.Should().Be(job.Id);
        report.Report!.Status.Should().Be(ReportStatus.Done);
        report.BodyText.Should().Be(ProbeHandler.AcknowledgementPrefix + "home-laptop\n");
        (await _bus.ExistsOnMainAsync(job.RelativePath, ct)).Should().BeFalse();
        (await _bus.ExistsOnMainAsync(BusPaths.EnvelopeFile(BusPaths.Claimed(Acme, HomeLaptop), job.Id), ct)).Should().BeFalse();
        var info = _log.AtLevel(LogLevel.Information).Where(e => e.Category.EndsWith("BusListener", StringComparison.Ordinal)).ToList();
        info.Select(e => e.Scope?["phase"]).Should().Equal("claim", "report");
        _log.Entries.Where(e => e.Category.EndsWith("BusListener", StringComparison.Ordinal)).Should().AllSatisfy(e =>
        {
            e.Scope.Should().NotBeNull();
            e.Scope!.Should().ContainKey("tenant").WhoseValue.Should().Be("acme");
            e.Scope.Should().ContainKey("machine").WhoseValue.Should().Be("home-laptop");
        });
        _log.Entries.Where(e => e.Category.EndsWith("BusListener", StringComparison.Ordinal)).Should().HaveCount(2, "a 304 tick is silent");
        SecretSweep.AssertAbsent(_hooks.Calls.SelectMany(SecretSweep.TextsOf));
        SecretSweep.AssertAbsent(_log.Messages);
    }

    [Fact]
    public async Task ProbeHeadAsync_SequenceIsChangedThenUnchanged()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using ServiceProvider services = TestHost.BuildListener(_bus, HomeLaptop, _store);
        IBusProvider provider = services.GetRequiredService<IBusProvider>();

        // Act
        var first = (Changed)await provider.ProbeHeadAsync(null, ct);
        HeadProbe second = await provider.ProbeHeadAsync(first.ETag, ct);
        await _bus.AdvanceMainFromSenderAsync(ct);
        HeadProbe third = await provider.ProbeHeadAsync(first.ETag, ct);

        // Assert
        second.Should().BeOfType<Unchanged>();
        third.Should().BeOfType<Changed>().Which.ETag.Should().NotBe(first.ETag);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ProcessOnceAsync_RejectionCases_RejectedOnMainHandlerNeverCalled(string variant)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using ServiceProvider services = TestHost.BuildListener(_bus, HomeLaptop, _store, CountingHandler(), _hooks, _log);
        BusListener listener = services.GetRequiredService<BusListener>();
        (await listener.StartAsync(ct)).Should().Be(BusStartupResult.Clean);
        string path;
        bool reportExpected = true;
        switch (variant)
        {
            case "flipped":
                path = (await _bus.SeedSignedJobAsync(Central, HomeLaptop, TestKeys.Acme1, _store, "probe\n", mutate: b => { b[^2] ^= 0x01; return b; }, ct: ct)).RelativePath;
                break;
            case "to-central":
                var id = EnvelopeId.New(DateTimeOffset.UtcNow);
                path = BusPaths.EnvelopeFile(BusPaths.Jobs(Acme, HomeLaptop), id);
                await _bus.SeedRawFileAsync(path, await TestEnvelopes.SignedJobBytesAsync(Central, Central, _store, TestKeys.Acme1, "probe\n", ct, id: id), "misfiled", ct);
                break;
            case "schema2":
                path = (await _bus.SeedSignedJobAsync(Central, HomeLaptop, TestKeys.Acme1, _store, "probe\n", mutate: b => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(b).Replace("schema: 1", "schema: 2", StringComparison.Ordinal)), ct: ct)).RelativePath;
                break;
            default:
                path = BusPaths.Jobs(Acme, HomeLaptop) + "/weird.md";
                await _bus.SeedRawFileAsync(path, Encoding.UTF8.GetBytes("not an envelope\n"), "raw", ct);
                reportExpected = false;
                break;
        }

        (await services.GetRequiredService<IBusProvider>().PullAsync(ct)).Succeeded.Should().BeTrue();

        // Act
        int handled = await listener.ProcessOnceAsync(ct);

        // Assert
        handled.Should().Be(1);
        _handlerCalls.Should().Be(0);
        (await _bus.ExistsOnMainAsync(path, ct)).Should().BeFalse();
        (await _bus.RunGitAsync(_bus.BareDir, ["ls-tree", "--name-only", "main", BusPaths.Rejected(Acme, HomeLaptop) + "/"], ct))
            .Split('\n').Should().Contain(p => p.EndsWith(".md", StringComparison.Ordinal));
        (await ReportPathsAsync(BusPaths.Reports(Acme, HomeLaptop), ct)).Should().HaveCount(reportExpected ? 1 : 0);
        _log.Entries.Where(e => e.Category.EndsWith("BusListener", StringComparison.Ordinal)).Should().BeEmpty("rejection is the repository's warning, not a listener state change");
        SecretSweep.AssertAbsent(_hooks.Calls.SelectMany(SecretSweep.TextsOf));
        SecretSweep.AssertAbsent(_log.Messages);
    }

    [Fact]
    public async Task ProcessOnceAsync_AfterDeferredClaim_NextWritePushesItAndLogsPushedDeferred()
    {
        // Arrange: every push fails during the first pass
        var ct = TestContext.Current.CancellationToken;
        await using ServiceProvider services = TestHost.BuildListener(_bus, HomeLaptop, _store, hooks: _hooks, logging: _log);
        BusListener listener = services.GetRequiredService<BusListener>();
        (await listener.StartAsync(ct)).Should().Be(BusStartupResult.Clean);
        SeededJob first = await _bus.SeedSignedJobAsync(Central, HomeLaptop, TestKeys.Acme1, _store, "probe\n", ct: ct);
        (await services.GetRequiredService<IBusProvider>().PullAsync(ct)).Succeeded.Should().BeTrue();
        _hooks.Override = spec => HookedProcessRunner.SubCommand(spec) == "push"
            ? new ProcessResult(1, string.Empty, "! [rejected] main -> main (fetch first)", false, false, false, TimeSpan.Zero)
            : null;
        (await listener.ProcessOnceAsync(ct)).Should().Be(1);
        (await _bus.RevListCountAsync("origin/main..HEAD", ct)).Should().BeGreaterThan(0);
        _hooks.Override = null;
        SeededJob second = await _bus.SeedSignedJobAsync(Central, HomeLaptop, TestKeys.Acme1, _store, "probe\n", ct: ct);
        (await services.GetRequiredService<IBusProvider>().PullAsync(ct)).Succeeded.Should().BeTrue();

        // Act
        (await listener.ProcessOnceAsync(ct)).Should().Be(1);

        // Assert: both jobs answered on main, nothing left unpushed, the deferred line logged once
        (await _bus.RevListCountAsync("origin/main..HEAD", ct)).Should().Be(0);
        (await ReportPathsAsync(BusPaths.Reports(Acme, HomeLaptop), ct)).Should().HaveCount(2);
        (await _bus.ExistsOnMainAsync(first.RelativePath, ct)).Should().BeFalse();
        (await _bus.ExistsOnMainAsync(second.RelativePath, ct)).Should().BeFalse();
        _log.Messages.Where(m => m.StartsWith("pushed ", StringComparison.Ordinal) && m.Contains("deferred", StringComparison.Ordinal)).Should().ContainSingle();
        SecretSweep.AssertAbsent(_hooks.Calls.SelectMany(SecretSweep.TextsOf));
        SecretSweep.AssertAbsent(_log.Messages);
    }

    private JobHandler CountingHandler() => (_, _) =>
    {
        _handlerCalls++;
        return Task.FromResult(new JobReply(ReportStatus.Done, null, ReadOnlyMemory<byte>.Empty, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
    };

    private async Task<IReadOnlyList<string>> ReportPathsAsync(string folder, CancellationToken ct)
    {
        string output = await _bus.RunGitAsync(_bus.BareDir, ["ls-tree", "--name-only", "main", folder + "/"], ct);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(p => p.EndsWith(".md", StringComparison.Ordinal)).ToList();
    }
}
