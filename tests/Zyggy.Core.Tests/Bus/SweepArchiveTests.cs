using System.Text;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Bus;

/// <summary>
/// Spec 17 slice 5 (AC-11, AC-5 archive half): which <c>reports/</c> and <c>context/</c> files Central's sweep archives, through the git
/// and bus seams. Now is 2026-11-14 12:00 UTC.
/// </summary>
public sealed class SweepArchiveTests : IDisposable
{
    private static readonly TenantId Acme = SweepClaimsHarness.Acme;

    private static readonly MachineName Central = SweepClaimsHarness.Central;

    private static readonly MachineName HomeLaptop = SweepClaimsHarness.HomeLaptop;

    private static readonly MachineName WorkLaptop = SweepClaimsHarness.WorkLaptop;

    private static readonly DateTimeOffset Now = SweepClaimsHarness.Now;

    private readonly SweepClaimsHarness _h = new();

    public void Dispose() => _h.Dispose();

    // AC-11: files older than 14 days in reports/ and context/ — of any machine, Central's included — move to the month of their created, in one commit.
    [Fact]
    public async Task RunAsync_ReportAndContextOlderThanFourteenDays_ArchivedByCreatedMonthInOneCommit()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        DateTimeOffset fifteen = Now.AddDays(-15);
        DateTimeOffset twenty = Now.AddDays(-20);
        (string report, EnvelopeId reportId) = await DropAsync(BusFolder.Reports, HomeLaptop, WorkLaptop, fifteen, ct);
        (string context, EnvelopeId contextId) = await DropAsync(BusFolder.Context, Central, Central, twenty, ct);
        await DropAsync(BusFolder.Reports, HomeLaptop, WorkLaptop, Now.AddDays(-13), ct);
        BusSweep sweep = await _h.SweepAsync(ct);

        // Act
        SweepResult result = await sweep.RunAsync(ct);

        // Assert
        result.Archived.Should().Be(2);
        result.OldestArchived.Should().Be(twenty);
        _h.Processes.CallsOf("mv").Select(c => (c.Arguments[2], c.Arguments[3])).Should().BeEquivalentTo(
            [(context, Archived(contextId, twenty)), (report, Archived(reportId, fifteen))]);
        _h.Processes.CallsOf("commit").Should().ContainSingle().Which.StandardInput.Should().StartWith("acme archive 2 central→archive");
    }

    // AC-11: only a file strictly older than 14 days is selected.
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    public async Task RunAsync_ReportAtOrPastFourteenDays_ArchivedOnlyWhenStrictlyOlder(int secondsPast, int expected)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropAsync(BusFolder.Reports, HomeLaptop, WorkLaptop, Now.AddDays(-14).AddSeconds(-secondsPast), ct);
        BusSweep sweep = await _h.SweepAsync(ct);

        // Act
        SweepResult result = await sweep.RunAsync(ct);

        // Assert
        result.Archived.Should().Be(expected);
        _h.Processes.CallsOf("mv").Should().HaveCount(expected);
    }

    // AC-11: a file the pickup still takes (verifies, written by another machine into its own folder, addressed to Central) is never
    // selected, whatever its age; one the pickup leaves or does not take is.
    [Theory]
    [InlineData(BusFolder.Reports, "home-laptop", "central", false, 0)]
    [InlineData(BusFolder.Context, "home-laptop", "central", false, 0)]
    [InlineData(BusFolder.Reports, "home-laptop", "central", true, 1)]
    [InlineData(BusFolder.Context, "home-laptop", "central", true, 1)]
    [InlineData(BusFolder.Reports, "central", "central", false, 1)]
    [InlineData(BusFolder.Reports, "home-laptop", "work-laptop", false, 1)]
    public async Task RunAsync_OldFileThePickupMayTake_SkippedOnlyWhenItVerifiesForCentral(BusFolder kind, string folder, string to, bool tampered, int expected)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Func<byte[], byte[]>? mutate = tampered ? b => Replace(b, "note body", "tampered") : null;
        await DropAsync(kind, MachineName.Parse(folder), MachineName.Parse(to), Now.AddDays(-30), ct, mutate);
        BusSweep sweep = await _h.SweepAsync(ct);

        // Act
        SweepResult result = await sweep.RunAsync(ct);

        // Assert
        result.Archived.Should().Be(expected);
        _h.Processes.CallsOf("mv").Should().HaveCount(expected);
    }

    // AC-11: a file written by another machine than the folder's owner is left by the pickup, so it is archived when old.
    [Fact]
    public async Task RunAsync_OldReportInAnotherMachinesFolderToCentral_Archived()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        (string path, EnvelopeId id) = await DropAsync(BusFolder.Reports, HomeLaptop, Central, Now.AddDays(-30), ct, from: WorkLaptop);
        BusSweep sweep = await _h.SweepAsync(ct);

        // Act
        SweepResult result = await sweep.RunAsync(ct);

        // Assert
        result.Archived.Should().Be(1);
        _h.Processes.CallsOf("mv").Should().ContainSingle().Which.Arguments.Should().Equal("mv", "--", path, Archived(id, Now.AddDays(-30)));
    }

    // AC-11: items/ and jobs/ folders are never archived, whatever the age of what they hold.
    [Theory]
    [InlineData(BusFolder.Items)]
    [InlineData(BusFolder.Jobs)]
    [InlineData(BusFolder.Rejected)]
    public async Task RunAsync_OldEnvelopeInItemsOrJobsFolder_NeverArchived(BusFolder kind)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        DateTimeOffset old = Now.AddDays(-60);
        byte[] bytes = await TestEnvelopes.SignedReportBytesAsync(HomeLaptop, WorkLaptop, EnvelopeId.New(old), await _h.StoreAsync(ct), TestKeys.Acme1, "note body\n", ct, created: old);
        string folder = kind switch
        {
            BusFolder.Items => BusPaths.Items(Acme, HomeLaptop),
            BusFolder.Jobs => BusPaths.Jobs(Acme, HomeLaptop),
            _ => BusPaths.Rejected(Acme, HomeLaptop),
        };
        _h.Checkout.Drop(BusPaths.EnvelopeFile(folder, EnvelopeId.New(old)), bytes);
        BusSweep sweep = await _h.SweepAsync(ct);

        // Act
        SweepResult result = await sweep.RunAsync(ct);

        // Assert
        result.Should().BeEquivalentTo(new SweepResult([], null, false));
        _h.Processes.Calls.Should().BeEmpty();
    }

    // AC-11: a file whose created cannot be read is aged and filed by the commit that added it.
    [Theory]
    [InlineData(15, 1)]
    [InlineData(10, 0)]
    public async Task RunAsync_UnparsableReport_SelectedByItsAddingCommit(int addedDaysAgo, int expected)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        DateTimeOffset added = Now.AddDays(-addedDaysAgo);
        EnvelopeId id = EnvelopeId.New(Now.AddDays(-40));
        string path = BusPaths.EnvelopeFile(BusPaths.Reports(Acme, HomeLaptop), id);
        _h.Checkout.Drop(path, Encoding.UTF8.GetBytes("not an envelope\n"));
        _h.AddedAt(path, added);
        BusSweep sweep = await _h.SweepAsync(ct);

        // Act
        SweepResult result = await sweep.RunAsync(ct);

        // Assert
        result.Archived.Should().Be(expected);
        _h.Processes.CallsOf("mv").Select(c => c.Arguments[3]).Should().Equal(expected == 1 ? [Archived(id, added)] : []);
    }

    // AC-11: an unparsable file whose adding commit git cannot name has no age; it stays and the pass goes on without a failure.
    [Fact]
    public async Task RunAsync_UnparsableReportWithoutAddingCommit_LeftPassSucceeds()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        _h.Checkout.Drop(BusPaths.EnvelopeFile(BusPaths.Reports(Acme, HomeLaptop), EnvelopeId.New(Now)), Encoding.UTF8.GetBytes("not an envelope\n"));
        BusSweep sweep = await _h.SweepAsync(ct);

        // Act
        SweepResult result = await sweep.RunAsync(ct);

        // Assert
        result.Should().BeEquivalentTo(new SweepResult([], null, false));
        _h.Processes.CallsOf("mv").Should().BeEmpty();
    }

    // AC-5 (archive half): without a usable signing key no claim is touched, and the archive step of the same pass still moves its files.
    [Fact]
    public async Task RunAsync_CentralSigningKeyMissing_ClaimsUntouchedArchiveStillMoves()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await _h.StoreAsync(ct, centralKey: false);
        (string claim, _, _) = await _h.DropClaimAsync(HomeLaptop, ct);
        (string report, EnvelopeId id) = await DropAsync(BusFolder.Reports, HomeLaptop, WorkLaptop, Now.AddDays(-15), ct);
        BusSweep sweep = await _h.SweepAsync(ct);

        // Act
        SweepResult result = await sweep.RunAsync(ct);

        // Assert
        result.Actions.Should().BeEmpty();
        result.Archived.Should().Be(1);
        _h.Checkout.Exists(claim).Should().BeTrue();
        _h.Processes.CallsOf("mv").Should().ContainSingle().Which.Arguments.Should().Equal("mv", "--", report, Archived(id, Now.AddDays(-15)));
        _h.Log.AtLevel(LogLevel.Error).Should().ContainSingle().Which.Message.Should().StartWith("sweep: cannot sign: ");
    }

    // AC-17: one Information line per archive commit, with the count, the oldest created and whether the push went through.
    [Theory]
    [InlineData(true, "pushed")]
    [InlineData(false, "deferred")]
    public async Task RunAsync_ArchiveCommitted_OneInformationLineWithScope(bool pushAccepted, string state)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        if (!pushAccepted)
        {
            _h.Provider.PushAsync(Arg.Any<CancellationToken>()).Returns(new Rejected("non-fast-forward"));
        }

        await DropAsync(BusFolder.Reports, HomeLaptop, WorkLaptop, Now.AddDays(-15), ct);
        await DropAsync(BusFolder.Context, WorkLaptop, HomeLaptop, Now.AddDays(-20), ct);
        BusSweep sweep = await _h.SweepAsync(ct);

        // Act: a refused push retries after jittered delays on the fake clock.
        Task<SweepResult> pass = sweep.RunAsync(ct);
        while (!pass.IsCompleted)
        {
            _h.Clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(1, ct);
        }

        SweepResult result = await pass;

        // Assert
        result.PushDeferred.Should().Be(!pushAccepted);
        _h.Log.Entries.Where(e => e.Message.StartsWith("archived", StringComparison.Ordinal)).Select(e => (e.Level, e.Message)).Should().Equal(
            (LogLevel.Information, $"archived 2 file(s) older than 14 days, oldest created 2026-10-25T12:00:00Z; {state}"));
        _h.Log.Entries.Should().OnlyContain(e => e.Scopes.OfType<Dictionary<string, object>>().Any(
            s => (string)s["tenant"] == "acme" && (string)s["machine"] == "central" && (string)s["phase"] == "sweep"));
    }

    private static string Archived(EnvelopeId id, DateTimeOffset created) =>
        BusPaths.EnvelopeFile(BusPaths.Archive(Acme, created.UtcDateTime.Year, created.UtcDateTime.Month), id);

    private static byte[] Replace(byte[] bytes, string from, string to) =>
        Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace(from, to, StringComparison.Ordinal));

    /// <summary>A signed report or context written by <paramref name="from"/> (the folder's machine by default), dropped in <paramref name="folder"/>'s <paramref name="kind"/> folder.</summary>
    private async Task<(string Path, EnvelopeId Id)> DropAsync(
        BusFolder kind, MachineName folder, MachineName to, DateTimeOffset created, CancellationToken ct, Func<byte[], byte[]>? mutate = null, MachineName? from = null)
    {
        EnvelopeId id = EnvelopeId.New(created);
        var store = await _h.StoreAsync(ct);
        byte[] bytes = kind == BusFolder.Context
            ? await TestEnvelopes.SignedContextBytesAsync(from ?? folder, to, store, TestKeys.Acme1, "note body\n", created, ct, id: id, mutate: mutate)
            : await TestEnvelopes.SignedReportBytesAsync(from ?? folder, to, EnvelopeId.New(created), store, TestKeys.Acme1, "note body\n", ct, mutate, id: id, created: created);
        string path = BusPaths.EnvelopeFile(kind == BusFolder.Context ? BusPaths.Context(Acme, folder) : BusPaths.Reports(Acme, folder), id);
        _h.Checkout.Drop(path, bytes);
        return (path, id);
    }
}
