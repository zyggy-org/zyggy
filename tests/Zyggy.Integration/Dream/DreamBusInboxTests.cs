using System.Text.Json;

using Zyggy.Core.Memory;
using Zyggy.Core.Tenancy;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Dream;

/// <summary>
/// Spec 13 AC-30 and AC-3 (hand-over): the nightly dream files and consumes what Central's pickup wrote — a context's stated line and
/// a report's one observed line — after which <c>get_context</c> no longer shows them in the inbox block. The dream never touches the bus.
/// </summary>
public sealed class DreamBusInboxTests : IAsyncLifetime
{
    private const string ContextLine = "- [stated] 2026-09-29 (project:calizr): The staging database is restored from Sunday's snapshot";

    private const string ReportLine = "- [observed] 2026-09-29 [report 01K8ZX2A]: Root cause is the expired staging certificate";

    private const string ContextFile = "inbox/bus-context-home-laptop-01K8ZX3P4Q5R6S7T8V9W0XYZAB-2026-09-29.md";

    private const string ReportFile = "inbox/bus-report-home-laptop-01K8ZX2A3B4C5D6E7F8G9H0JKM-01K8ZX4Q5R6S7T8V9W0XYZABCD-2026-09-29.md";

    private readonly MemoryRepoFixture _repo = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _repo.InitializeAsync();
        await _repo.SeedAsync("dream-bus", Ct);
    }

    public ValueTask DisposeAsync() => _repo.DisposeAsync();

    [Fact]
    public async Task Dream_BusContextAndReportLines_FiledAndConsumed_InboxFilesNeverCommitted()
    {
        // Arrange
        string inbox = Path.Combine(_repo.PrincipalDir, "inbox");
        var before = Directory.EnumerateFiles(inbox).ToDictionary(f => f, File.ReadAllText);

        // Act
        var run = await _repo.DreamAsync("dream-file-bus", Ct);

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr + run.Stdout);
        (await _repo.ShowAsync("acme/alice/business/areas/calizr.md", Ct)).Should().Contain(ContextLine).And.Contain(ReportLine);
        using var ledger = JsonDocument.Parse(await _repo.ShowAsync("acme/alice/.dream/ledger.json", Ct));
        var files = ledger.RootElement.GetProperty("files");
        files.GetProperty(ContextFile).GetProperty("consumed").GetArrayLength().Should().Be(1);
        files.GetProperty(ReportFile).GetProperty("consumed").GetArrayLength().Should().Be(1);
        Directory.EnumerateFiles(inbox).ToDictionary(f => f, File.ReadAllText).Should().BeEquivalentTo(before);
        (await _repo.LastCommitPathsAsync(Ct)).Should().NotContain(p => p.Contains("/inbox/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetContext_AfterDream_LinesNoLongerInInboxBlock()
    {
        // Arrange
        var paths = new MemoryPaths(_repo.CloneDir, new Principal(TenantId.Parse("acme"), UserId.Parse("alice")));
        string beforeDatabase;
        await using (InProcessHub hub = await InProcessHub.StartAsync(paths, TimeZoneInfo.Utc))
        {
            beforeDatabase = await hub.CallGetContextAsync("staging database");
        }

        // Act
        var run = await _repo.DreamAsync("dream-file-bus", Ct);
        string database;
        string certificate;
        string calizr;
        await using (InProcessHub hub = await InProcessHub.StartAsync(paths, TimeZoneInfo.Utc))
        {
            database = await hub.CallGetContextAsync("staging database");
            certificate = await hub.CallGetContextAsync("staging certificate");
            calizr = await hub.CallGetContextAsync("calizr");
        }

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        beforeDatabase.Should().Contain("## inbox (picked up, not yet filed by the dream)\n").And.Contain(ContextLine + "\n").And.Contain(ReportLine + "\n");
        database.Should().NotContain("## inbox (");
        certificate.Should().NotContain("## inbox (");
        // get_context matches a file by name, aliases and description, never by body (spec 11): the filed lines come back under the file
        calizr.Should().Contain("## business/areas/calizr.md — The calizr project\n" + ContextLine + "\n" + ReportLine + "\n");
    }
}
