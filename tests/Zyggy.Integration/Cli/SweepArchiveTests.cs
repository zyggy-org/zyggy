using System.Globalization;
using System.Text;

using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Cli;

/// <summary>
/// Spec 17 slice 6 (AC-12, AC-13): <c>zyggy sweep</c> in process on Central's checkout of a bare repository moves old <c>reports/</c>
/// and <c>context/</c> files to <c>archive/YYYY-MM/</c> in one commit with their bytes unchanged, and <c>zyggy sweep --dry-run</c>
/// prints the same pass without pulling or writing.
/// </summary>
public sealed class SweepArchiveTests : IAsyncLifetime
{
    private static readonly MachineName WorkLaptop = MachineName.Parse("work-laptop");

    /// <summary>Noon UTC today, so envelope <c>created</c> values round-trip to the second.</summary>
    private static readonly DateTimeOffset Today = new(DateTime.UtcNow.Date.AddHours(12), TimeSpan.Zero);

    private CliHarness _harness = null!;

    private ScratchProject _project = null!;

    public async ValueTask InitializeAsync()
    {
        _harness = await CliHarness.CreateAsync(CancellationToken.None);
        _project = await ScratchProject.CreateAsync(_harness.Bus, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        _project?.Dispose();
        await _harness.DisposeAsync();
    }

    // AC-12 + AC-13: each old file under archive/<YYYY-MM of created>/ in one commit with one body line per move, bytes unchanged;
    // a young file stays; the verb prints the archive line and the summary and exits 0.
    [Fact]
    public async Task Sweep_OldReportAndContext_ArchivedInOneCommitBytesUnchanged()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        DateTimeOffset thirty = Today.AddDays(-30);
        DateTimeOffset twenty = Today.AddDays(-20);
        SeededJob report = await _harness.Bus.SeedSignedReportAsync(
            CliHarness.HomeLaptop, WorkLaptop, EnvelopeId.New(thirty), TestKeys.Acme1, _harness.Store, "done\n", created: thirty, ct: ct);
        SeededJob context = await _harness.Bus.SeedSignedContextAsync(WorkLaptop, CliHarness.HomeLaptop, TestKeys.Acme1, _harness.Store, "note\n", created: twenty, ct: ct);
        SeededJob young = await _harness.Bus.SeedSignedReportAsync(
            CliHarness.HomeLaptop, WorkLaptop, EnvelopeId.New(Today), TestKeys.Acme1, _harness.Store, "done\n", created: Today.AddDays(-1), ct: ct);
        string reportBlob = await BlobOnMainAsync(report.RelativePath, ct);
        string contextBlob = await BlobOnMainAsync(context.RelativePath, ct);
        string reportTarget = Archived(report.Id, thirty);
        string contextTarget = Archived(context.Id, twenty);

        // Act
        var (exit, console) = await SweepAsync(["sweep"], TimeProvider.System, ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        console.Stdout.Should().Be($"archived 2 file(s), oldest created {Utc(thirty)}\nsweep: 0 re-queued, 0 exhausted, 0 rejected, 2 archived\n");
        (await _harness.Bus.LastCommitSubjectAsync(ct)).Trim().Should().Be("acme archive 2 central→archive");
        (await LastCommitBodyAsync(ct)).Should().BeEquivalentTo($"{report.RelativePath} → {reportTarget}", $"{context.RelativePath} → {contextTarget}");
        (await BlobOnMainAsync(reportTarget, ct)).Should().Be(reportBlob);
        (await BlobOnMainAsync(contextTarget, ct)).Should().Be(contextBlob);
        (await _harness.Bus.ExistsOnMainAsync(report.RelativePath, ct)).Should().BeFalse();
        (await _harness.Bus.ExistsOnMainAsync(context.RelativePath, ct)).Should().BeFalse();
        (await _harness.Bus.ExistsOnMainAsync(young.RelativePath, ct)).Should().BeTrue();
    }

    // AC-12: a file that does not parse is aged by, and filed in the month of, the commit that added it.
    [Fact]
    public async Task Sweep_UnparsableReport_ArchivedInMonthOfAddingCommit()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        EnvelopeId id = EnvelopeId.New(Today);
        string path = BusPaths.EnvelopeFile(BusPaths.Reports(CliHarness.Acme, CliHarness.HomeLaptop), id);
        await _harness.Bus.SeedRawFileAsync(path, Encoding.UTF8.GetBytes("not an envelope\n"), "acme report " + id.Value + " home-laptop→work-laptop", ct);
        string blob = await BlobOnMainAsync(path, ct);
        DateTimeOffset added = DateTimeOffset.Parse(
            (await _harness.Bus.RunGitAsync(_harness.Bus.BareDir, ["log", "-1", "--format=%cI", "main", "--", path], ct)).Trim(), CultureInfo.InvariantCulture);
        string target = Archived(id, added.ToUniversalTime());

        // Act: Central's clock 20 days ahead, so the commit that added the file is past 14 days.
        var (exit, console) = await SweepAsync(["sweep"], new OffsetClock(TimeSpan.FromDays(20)), ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        (await LastCommitBodyAsync(ct)).Should().Contain($"{path} → {target}");
        (await BlobOnMainAsync(target, ct)).Should().Be(blob);
        (await _harness.Bus.ExistsOnMainAsync(path, ct)).Should().BeFalse();
    }

    // AC-13: --dry-run prints the checkout's sha, the same actions and the dry-run summary; it does not pull and leaves the remote,
    // the checkout's HEAD and its working tree unchanged.
    [Fact]
    public async Task SweepDryRun_StaleClaimAndOldReport_PrintsPassWritesNothingPullsNothing()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Model job = await SeedClaimAsync(ct);
        DateTimeOffset thirty = Today.AddDays(-30);
        SeededJob report = await _harness.Bus.SeedSignedReportAsync(
            CliHarness.HomeLaptop, WorkLaptop, EnvelopeId.New(thirty), TestKeys.Acme1, _harness.Store, "done\n", created: thirty, ct: ct);
        string head = await HeadAsync(_harness.Bus.SenderDir, ct);
        await PushFromAnotherCloneAsync(ct);
        string remote = await HeadAsync(_harness.Bus.BareDir, ct);

        // Act
        var (exit, console) = await SweepAsync(["sweep", "--dry-run"], new OffsetClock(TimeSpan.FromHours(1)), ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        console.Stdout.Should().Be(
            $"checkout at {head[..7]}\n" +
            $"requeued {job.Header.Id.Value} home-laptop attempt 2\n" +
            $"archived 1 file(s), oldest created {Utc(thirty)}\n" +
            "dry run: sweep: 1 re-queued, 0 exhausted, 0 rejected, 1 archived\n");
        (await HeadAsync(_harness.Bus.BareDir, ct)).Should().Be(remote);
        (await HeadAsync(_harness.Bus.SenderDir, ct)).Should().Be(head, "a dry run does not pull");
        (await _harness.PorcelainAsync(_harness.Bus.SenderDir, ct)).Should().BeEmpty();
        File.Exists(BusPaths.Resolve(_harness.Bus.SenderDir, ClaimedPath(job))).Should().BeTrue();
        File.Exists(BusPaths.Resolve(_harness.Bus.SenderDir, report.RelativePath)).Should().BeTrue();
    }

    private static string Utc(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string Archived(EnvelopeId id, DateTimeOffset created) =>
        BusPaths.EnvelopeFile(BusPaths.Archive(CliHarness.Acme, created.UtcDateTime.Year, created.UtcDateTime.Month), id);

    private static string ClaimedPath(Model job) => BusPaths.EnvelopeFile(BusPaths.Claimed(CliHarness.Acme, CliHarness.HomeLaptop), job.Header.Id);

    // A job central → home-laptop in home-laptop's jobs/claimed/, committed now from Central's checkout (the sender clone).
    private async Task<Model> SeedClaimAsync(CancellationToken ct)
    {
        Model job = await TestEnvelopes.SignedJobAsync(
            CliHarness.Central,
            CliHarness.HomeLaptop,
            _harness.Store,
            TestKeys.Acme1,
            "Read README.md and finish with a REPORT section; change nothing.\n",
            ct,
            fields: new JobFields(_project.Path, TimeoutMinutes: 2, AllowedTools: ["Read"]));
        await _harness.Bus.SeedRawFileAsync(ClaimedPath(job), EnvelopeWriter.Serialize(job), "acme claim " + job.Header.Id.Value + " home-laptop", ct);
        return job;
    }

    // One commit pushed from a third checkout, so the remote is ahead of Central's checkout.
    private async Task PushFromAnotherCloneAsync(CancellationToken ct)
    {
        string other = await _harness.Bus.CloneAsync("other", ct);
        await File.AppendAllTextAsync(BusPaths.Resolve(other, BusPaths.Protocol), "\nnoise\n", ct);
        await _harness.Bus.RunGitAsync(other, ["commit", "-am", "noise"], ct);
        await _harness.Bus.RunGitAsync(other, ["push", "origin", "HEAD:main"], ct);
    }

    private async Task<string> HeadAsync(string repository, CancellationToken ct) =>
        (await _harness.Bus.RunGitAsync(repository, ["rev-parse", repository == _harness.Bus.BareDir ? "main" : "HEAD"], ct)).Trim();

    private async Task<string> BlobOnMainAsync(string path, CancellationToken ct) =>
        (await _harness.Bus.RunGitAsync(_harness.Bus.BareDir, ["rev-parse", "main:" + path], ct)).Trim();

    private async Task<string[]> LastCommitBodyAsync(CancellationToken ct) =>
        (await _harness.Bus.RunGitAsync(_harness.Bus.BareDir, ["log", "-1", "--format=%b", "main"], ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private Task<(int Exit, VerbConsole Console)> SweepAsync(string[] args, TimeProvider clock, CancellationToken ct) =>
        _harness.RunAsync(args, _harness.CentralNodeJson, more: s => s.AddSingleton(clock), ct: ct);

    /// <summary>The system clock moved ahead by a fixed amount; timers stay real.</summary>
    private sealed class OffsetClock(TimeSpan offset) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + offset;
    }
}
