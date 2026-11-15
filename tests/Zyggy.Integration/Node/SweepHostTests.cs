using System.Net;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Processes;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;
using Zyggy.Node;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Node;

/// <summary>
/// Spec 17 slice 7 (AC-15, AC-16a) through the production host: the node service on Central sweeps after the start-up pull and
/// pickup and again every sweep interval; a laptop never sweeps; an out-of-range <c>sweep.intervalMinutes</c> refuses the start; a pass
/// whose git step fails leaves the host running and the next interval sweeps. Central's clock runs an hour ahead (timers stay real),
/// so a claim committed now is past its 2-minute timeout + 15 min; the interval is overridden to seconds.
/// </summary>
public sealed class SweepHostTests
{
    private const string Fact = "- [stated] 2026-10-29 (project:calizr): The staging database is restored from Sunday's snapshot\n";

    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName Central = MachineName.Central;

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly TimeSpan Ahead = TimeSpan.FromHours(1);

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);

    // AC-15: a pass after the start-up pickup, and one after every interval (three claims, seeded one after the other).
    [Fact]
    public async Task Central_StaleClaims_RequeuedAfterStartupPickupAndAfterEveryInterval()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using NodeHostHarness harness = await NodeHostHarness.CreateAsync(ct);
        using var memory = new PickupMemory();
        harness.WriteNodeJson(machine: Central);
        harness.Environment = memory.Env();
        SeededJob context = await harness.Bus.SeedSignedContextAsync(HomeLaptop, Central, TestKeys.Acme1, harness.Store, Fact, created: new DateTimeOffset(2026, 10, 29, 10, 42, 7, TimeSpan.Zero), ct: ct);
        Model first = await SeedClaimAsync(harness, HomeLaptop, Central, ct);
        Model? second = null;
        Model? third = null;

        // Act: each claim is seeded once the previous one is re-queued, so every interval after the first needs a pass of its own.
        int exit = await harness.RunUntilAsync(
            async () =>
            {
                if (second is null)
                {
                    if (await RequeuedAsync(harness, first, ct))
                    {
                        second = await SeedClaimAsync(harness, HomeLaptop, Central, ct);
                    }

                    return false;
                }

                if (third is null)
                {
                    if (await RequeuedAsync(harness, second, ct))
                    {
                        third = await SeedClaimAsync(harness, HomeLaptop, Central, ct);
                    }

                    return false;
                }

                return await RequeuedAsync(harness, third, ct);
            },
            TimeSpan.FromSeconds(60),
            ct,
            CentralTestServices);

        // Assert
        exit.Should().Be(0, string.Join('\n', harness.LogLines));
        IReadOnlyList<string> subjects = [.. (await harness.CommitSubjectsAsync(ct)).Reverse()];
        int pickup = IndexOf(subjects, "acme archive 1 central→archive");
        int firstRequeue = IndexOf(subjects, $"acme requeue {first.Header.Id.Value} central→home-laptop");
        int secondRequeue = IndexOf(subjects, $"acme requeue {second!.Header.Id.Value} central→home-laptop");
        pickup.Should().BeGreaterThanOrEqualTo(0, string.Join('\n', subjects));
        firstRequeue.Should().BeGreaterThan(pickup, "the start-up pass follows the start-up pickup");
        secondRequeue.Should().BeGreaterThan(firstRequeue);
        IndexOf(subjects, $"acme requeue {third!.Header.Id.Value} central→home-laptop").Should().BeGreaterThan(secondRequeue);
        (await harness.Bus.ExistsOnMainAsync(context.RelativePath, ct)).Should().BeFalse("the pickup took the context");
        harness.LogLines.Count(l => l.Contains("sweep every ", StringComparison.Ordinal)).Should().Be(1);
    }

    // AC-15: the start-up pass itself, at node.json's interval — with sweep.intervalMinutes 45 and no test override, only the pass after
    // the start-up pickup can re-queue the claim within the test, and the interval the node announces is the configured one.
    [Fact]
    public async Task Central_SweepIntervalFromNodeJson_StaleClaimRequeuedByStartupPass()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using NodeHostHarness harness = await NodeHostHarness.CreateAsync(ct);
        using var memory = new PickupMemory();
        string path = harness.WriteNodeJson(machine: Central);
        harness.Environment = memory.Env();
        JsonNode json = JsonNode.Parse(File.ReadAllText(path))!;
        json["sweep"] = new JsonObject { ["intervalMinutes"] = 45 };
        File.WriteAllText(path, json.ToJsonString());
        Model claim = await SeedClaimAsync(harness, HomeLaptop, Central, ct);

        // Act
        int exit = await harness.RunUntilAsync(
            () => RequeuedAsync(harness, claim, ct),
            TimeSpan.FromSeconds(60),
            ct,
            services => services.AddSingleton<TimeProvider>(new OffsetClock(Ahead)));

        // Assert
        exit.Should().Be(0, string.Join('\n', harness.LogLines));
        (await harness.Bus.ExistsOnMainAsync(ClaimedPath(HomeLaptop, claim), ct)).Should().BeFalse();
        harness.LogLines.Should().ContainSingle(l => l.Contains("sweep every 45 min", StringComparison.Ordinal));
    }

    // AC-15: a node that is not Central never sweeps, whatever its clock says.
    [Fact]
    public async Task Laptop_StaleClaimOfCentral_NeverSwept()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using NodeHostHarness harness = await NodeHostHarness.CreateAsync(ct);
        harness.WriteNodeJson(machine: HomeLaptop);
        Model claim = await SeedClaimAsync(harness, Central, HomeLaptop, ct);
        DateTimeOffset started = DateTimeOffset.UtcNow;

        // Act
        int exit = await harness.RunUntilAsync(
            () => Task.FromResult(
                harness.LogLines.Any(l => l.Contains("registry published", StringComparison.Ordinal))
                && DateTimeOffset.UtcNow - started > Interval * 3),
            TimeSpan.FromSeconds(60),
            ct,
            CentralTestServices);

        // Assert
        exit.Should().Be(0, string.Join('\n', harness.LogLines));
        (await harness.Bus.ExistsOnMainAsync(ClaimedPath(Central, claim), ct)).Should().BeTrue();
        (await harness.CommitSubjectsAsync(ct)).Should().NotContain(s => s.Contains(" requeue ", StringComparison.Ordinal));
        harness.LogLines.Should().NotContain(l => l.Contains("sweep", StringComparison.Ordinal));
    }

    // AC-15: an out-of-range interval refuses the start like every other node.json key.
    [Theory]
    [InlineData(0)]
    [InlineData(1441)]
    public async Task Central_SweepIntervalOutOfRange_StartRefusedExitThree(int minutes)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using NodeHostHarness harness = await NodeHostHarness.CreateAsync(ct);
        using var memory = new PickupMemory();
        string path = harness.WriteNodeJson(machine: Central);
        harness.Environment = memory.Env();
        JsonNode json = JsonNode.Parse(File.ReadAllText(path))!;
        json["sweep"] = new JsonObject { ["intervalMinutes"] = minutes };
        File.WriteAllText(path, json.ToJsonString());

        // Act
        Func<IHost> build = () => harness.Build();

        // Assert
        build.Should().Throw<NodeStartupException>()
            .Where(e => e.ExitCode == NodeExitCodes.Configuration && e.Message.Contains("sweep.intervalMinutes", StringComparison.Ordinal));
    }

    // AC-16a: a pass that fails inside the node service leaves the host running; /health answers and the next interval sweeps.
    [Fact]
    public async Task Central_SweepGitStepFails_HostRunsHealthAnswersNextIntervalRequeues()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using NodeHostHarness harness = await NodeHostHarness.CreateAsync(ct);
        using var memory = new PickupMemory();
        harness.WriteNodeJson(machine: Central);
        harness.Environment = memory.Env();
        Model claim = await SeedClaimAsync(harness, HomeLaptop, Central, ct);
        int refused = 0;
        harness.Hooks.Override = spec => HookedProcessRunner.SubCommand(spec) == "mv"
            && spec.Arguments.Any(a => a.Contains("/jobs/claimed/", StringComparison.Ordinal))
            && Interlocked.Increment(ref refused) == 1
            ? new ProcessResult(128, string.Empty, "fatal: refused by fixture", false, false, false, TimeSpan.Zero)
            : null;
        using IHost host = harness.Build(CentralTestServices);
        await host.StartAsync(ct);
        HealthServer server = host.Services.GetRequiredService<HealthServer>();
        await NodeHostHarness.WaitUntilAsync(
            () => Task.FromResult(server.IsListening && harness.LogLines.Any(l => l.Contains("sweep: failed at mv", StringComparison.Ordinal))),
            TimeSpan.FromSeconds(60),
            ct);

        // Act
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.BoundPort}/") };
        using HttpResponseMessage health = await client.GetAsync(new Uri("/health", UriKind.Relative), ct);
        await NodeHostHarness.WaitUntilAsync(() => RequeuedAsync(harness, claim, ct), TimeSpan.FromSeconds(60), ct);
        await host.StopAsync(ct);

        // Assert
        health.StatusCode.Should().Be(HttpStatusCode.OK);
        harness.LogLines.Count(l => l.Contains("sweep: failed at mv", StringComparison.Ordinal)).Should().Be(1, string.Join('\n', harness.LogLines));
        (await harness.Bus.ExistsOnMainAsync(ClaimedPath(HomeLaptop, claim), ct)).Should().BeFalse();
    }

    /// <summary>Central's test registrations: the clock an hour ahead and the sweep interval in seconds.</summary>
    private static void CentralTestServices(IServiceCollection services)
    {
        services.AddSingleton<TimeProvider>(new OffsetClock(Ahead));
        services.Configure<SweepScheduleOptions>(o => o.IntervalOverride = Interval);
    }

    private static int IndexOf(IReadOnlyList<string> subjects, string subject) =>
        subjects.Select((s, i) => (s, i)).Where(p => string.Equals(p.s.Trim(), subject, StringComparison.Ordinal)).Select(p => p.i).DefaultIfEmpty(-1).First();

    private static string ClaimedPath(MachineName machine, Model job) => BusPaths.EnvelopeFile(BusPaths.Claimed(Acme, machine), job.Header.Id);

    private static Task<bool> RequeuedAsync(NodeHostHarness harness, Model job, CancellationToken ct) =>
        harness.Bus.ExistsOnMainAsync(BusPaths.EnvelopeFile(BusPaths.Jobs(Acme, job.Header.To), job.Header.Id), ct);

    // A verifying job from → to sitting in to's jobs/claimed/, committed now.
    private static async Task<Model> SeedClaimAsync(NodeHostHarness harness, MachineName to, MachineName from, CancellationToken ct)
    {
        Model job = await TestEnvelopes.SignedJobAsync(from, to, harness.Store, TestKeys.Acme1, "Read README.md; change nothing.\n", ct, id: EnvelopeId.New(DateTimeOffset.UtcNow));
        await harness.Bus.SeedRawFileAsync(ClaimedPath(to, job), EnvelopeWriter.Serialize(job), "acme claim " + job.Header.Id.Value + " " + to.Value, ct);
        return job;
    }

    /// <summary>The system clock moved ahead by a fixed amount; timers stay real.</summary>
    private sealed class OffsetClock(TimeSpan offset) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + offset;
    }
}
