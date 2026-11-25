using Microsoft.Extensions.Logging;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Health;
using Zyggy.Core.Registry;
using Zyggy.Core.Tenancy;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Bus;

/// <summary>
/// Spec 61 slice 6 (AC-11): Central's sweep leaves a claim past its limit in place while the claiming machine's registry lists the id under
/// <c>running</c>; without the key (a 0.30.0 node) or once the id is gone it acts as today. A listed claim older than the longest a run can
/// live (24 h of wall-clock age, spec 61 §3) + the 15-minute grace is acted on as today, so a node that died never holds a claim forever.
/// </summary>
public sealed class SweepRunningTests : IDisposable
{
    private static readonly DateTimeOffset Seen = SweepClaimsHarness.Now - TimeSpan.FromMinutes(20);

    private readonly SweepClaimsHarness _h = new();

    public void Dispose() => _h.Dispose();

    // AC-11: listed under running → not re-queued, no commit, the claim reported as waiting with the registry's last_seen.
    [Fact]
    public async Task RunAsync_StaleClaimListedAsRunning_LeftInPlaceWaiting()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        (string path, Model job, byte[] bytes) = await _h.DropClaimAsync(SweepClaimsHarness.HomeLaptop, ct);
        DropRegistry(SweepClaimsHarness.HomeLaptop, [job.Header.Id], Seen);
        BusSweep sweep = await _h.SweepAsync(ct);

        // Act
        SweepResult result = await sweep.RunAsync(ct);

        // Assert
        new
        {
            result.Actions,
            result.Waiting,
            Commits = _h.Processes.CallsOf("commit").Count(),
            Claim = _h.Checkout.Read(path),
        }.Should().BeEquivalentTo(new
        {
            Actions = Array.Empty<SweepAction>(),
            Waiting = new[] { new SweepWaiting(job.Header.Id, SweepClaimsHarness.HomeLaptop, Seen) },
            Commits = 0,
            Claim = bytes,
        });
    }

    // AC-11: a registry without the key (what a 0.30.0 node writes) → re-queued exactly as today.
    [Fact]
    public async Task RunAsync_RegistryWithoutRunningKey_RequeuedAsToday()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        (_, Model job, _) = await _h.DropClaimAsync(SweepClaimsHarness.HomeLaptop, ct);
        DropRegistry(SweepClaimsHarness.HomeLaptop, [], Seen);
        BusSweep sweep = await _h.SweepAsync(ct);

        // Act
        SweepResult result = await sweep.RunAsync(ct);

        // Assert
        new { result.Actions, result.Waiting }.Should().BeEquivalentTo(new
        {
            Actions = new[] { new SweepAction(SweepActionKind.Requeued, job.Header.Id, SweepClaimsHarness.HomeLaptop, 2, null) },
            Waiting = Array.Empty<SweepWaiting>(),
        });
    }

    // AC-11: the registry no longer lists the id (the node reported or restarted without the run) → re-queued.
    [Fact]
    public async Task RunAsync_RegistryListsAnotherId_Requeued()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        (_, Model job, _) = await _h.DropClaimAsync(SweepClaimsHarness.HomeLaptop, ct);
        DropRegistry(SweepClaimsHarness.HomeLaptop, [EnvelopeId.Parse("01M87G38KYNXKBF5FCA14GHSGX")], Seen);
        BusSweep sweep = await _h.SweepAsync(ct);

        // Act
        SweepResult result = await sweep.RunAsync(ct);

        // Assert
        result.Actions.Should().Equal(new SweepAction(SweepActionKind.Requeued, job.Header.Id, SweepClaimsHarness.HomeLaptop, 2, null));
    }

    // AC-11: the registry of another machine listing the id does not hold this machine's claim.
    [Fact]
    public async Task RunAsync_AnotherMachineListsTheId_Requeued()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        (_, Model job, _) = await _h.DropClaimAsync(SweepClaimsHarness.HomeLaptop, ct);
        DropRegistry(SweepClaimsHarness.WorkLaptop, [job.Header.Id], Seen);
        BusSweep sweep = await _h.SweepAsync(ct);

        // Act
        SweepResult result = await sweep.RunAsync(ct);

        // Assert
        result.Requeued.Should().Be(1);
    }

    // Task bound: a listed claim older than 24 h + 15 min cannot have a live run (the node ends every run at 24 h of wall-clock age), so
    // the sweep acts on it as today; at the bound itself it still waits.
    [Theory]
    [InlineData(24 * 60 + 15, 0)]
    [InlineData(24 * 60 + 16, 1)]
    public async Task RunAsync_ListedClaimPastTheLongestRun_RequeuedOnlyPastTheBound(int claimedMinutesAgo, int requeued)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        (_, Model job, _) = await _h.DropClaimAsync(SweepClaimsHarness.HomeLaptop, ct, claimedAgo: TimeSpan.FromMinutes(claimedMinutesAgo));
        DropRegistry(SweepClaimsHarness.HomeLaptop, [job.Header.Id], Seen);
        BusSweep sweep = await _h.SweepAsync(ct);

        // Act
        SweepResult result = await sweep.RunAsync(ct);

        // Assert
        new { result.Requeued, Waiting = result.Waiting.Count }.Should().BeEquivalentTo(new { Requeued = requeued, Waiting = 1 - requeued });
    }

    // AC-11: a dry run reports the waiting claim and writes nothing.
    [Fact]
    public async Task RunAsync_DryRunListedClaim_WaitingNothingWritten()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        (_, Model job, _) = await _h.DropClaimAsync(SweepClaimsHarness.HomeLaptop, ct);
        DropRegistry(SweepClaimsHarness.HomeLaptop, [job.Header.Id], Seen);
        BusSweep sweep = await _h.SweepAsync(ct);

        // Act
        SweepResult result = await sweep.RunAsync(dryRun: true, ct);

        // Assert
        new { Waiting = result.Waiting.Select(w => w.Id), Commits = _h.Processes.CallsOf("commit").Count() }
            .Should().BeEquivalentTo(new { Waiting = new[] { job.Header.Id }, Commits = 0 });
    }

    // Spec 61 §3: one Information line per waiting claim per process, however many passes.
    [Fact]
    public async Task RunAsync_ListedClaimTwoPasses_OneInformationLine()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        (_, Model job, _) = await _h.DropClaimAsync(SweepClaimsHarness.HomeLaptop, ct);
        DropRegistry(SweepClaimsHarness.HomeLaptop, [job.Header.Id], Seen);
        BusSweep sweep = await _h.SweepAsync(ct);
        await sweep.RunAsync(ct);

        // Act
        await sweep.RunAsync(ct);

        // Assert
        _h.Log.AtLevel(LogLevel.Information).Select(e => e.Message).Should().Equal(
            $"waiting {job.Header.Id.Value} home-laptop still running, last seen 2026-11-14T11:40:00Z");
    }

    private void DropRegistry(MachineName machine, IReadOnlyList<EnvelopeId> running, DateTimeOffset lastSeen) =>
        _h.Checkout.Drop(
            BusPaths.Registry(SweepClaimsHarness.Acme, machine),
            new RegistryDocument(SweepClaimsHarness.Acme, machine, RegistryOs.Windows, lastSeen, HealthLevel.Green, null, []) { Version = "0.30.1", Running = running }.Serialize());
}
