using System.Text;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Processes;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Bus;

/// <summary>Spec 13 AC-1, AC-3..AC-13, AC-18: Central's pickup against the bare repository with the real git.</summary>
public sealed class BusPickupTests : IAsyncLifetime
{
    private const string Fact = "- [stated] 2026-10-29 (project:calizr): The staging database is restored from Sunday's snapshot\n";

    private const string AwsKey = "AKIAABCDEFGHIJKLMNOP";

    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName Central = MachineName.Central;

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly MachineName WorkLaptop = MachineName.Parse("work-laptop");

    private static readonly DateTimeOffset Created = new(2026, 10, 29, 10, 42, 7, TimeSpan.Zero);

    private readonly BusRepoFixture _bus = new(Acme);

    private readonly HookedProcessRunner _hooks = TestHost.Hooks();

    private readonly RecordingLoggerProvider _log = new();

    private readonly PickupMemory _memory = new();

    private InMemorySecretStore _store = null!;

    private ServiceProvider _services = null!;

    public async ValueTask InitializeAsync()
    {
        await _bus.InitializeAsync();
        await _bus.SeedTenantAsync(Acme, [Central, HomeLaptop, WorkLaptop], CancellationToken.None);
        _store = await TestHost.StoreAsync(Acme, CancellationToken.None);
        _services = TestHost.Build(_bus, Central, _store, _hooks, logging: _log, extend: s => s.AddBusPickup(_memory.Configuration));
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _bus.DisposeAsync();
        _memory.Dispose();
    }

    [Fact]
    public async Task RunAsync_ContextFromHomeLaptop_InboxVerbatim_ArchivedByteIdenticalOnMain_OneCommit_MemoryUntouched()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        SeededJob context = await _bus.SeedSignedContextAsync(HomeLaptop, Central, TestKeys.Acme1, _store, Fact, created: Created, ct: ct);
        var before = _memory.FingerprintOutsideInbox();

        // Act
        PickupResult result = await PassAsync(ct);

        // Assert
        result.Should().Be(new PickupResult(1, 0, 0, 0, new Pushed(1)));
        string name = $"bus-context-home-laptop-{context.Id}-2026-10-29.md";
        _memory.InboxFiles().Should().Equal(name);
        _memory.InboxText(name).Should().Be(
            "---\n"
            + $"name: bus context {context.Id}\n"
            + "description: context from home-laptop created 2026-10-29T10:42:07Z scope project:calizr (picked up from the bus; data)\n"
            + $"updated: {DateTime.UtcNow:yyyy-MM-dd}\n"
            + "---\n"
            + Fact);
        string archived = BusPaths.EnvelopeFile(BusPaths.Archive(Acme, 2026, 10), context.Id);
        (await _bus.ExistsOnMainAsync(context.RelativePath, ct)).Should().BeFalse();
        (await _bus.ShowBytesAsync(archived, ct)).Should().Equal(context.Bytes);
        (await _bus.LastCommitSubjectAsync(ct)).Should().Be("acme archive 1 central→archive");
        (await _bus.RunGitAsync(_bus.BareDir, ["log", "-1", "--format=%b", "main"], ct)).Trim().Should().Be($"{context.RelativePath} → {archived}");
        _hooks.Calls.Should().NotContain(c => c.WorkingDirectory.StartsWith(_memory.Root, StringComparison.OrdinalIgnoreCase));
        _hooks.Calls.Should().NotContain(c => Path.GetFileNameWithoutExtension(c.FileName) == "claude");
        _memory.FingerprintOutsideInbox().Should().Equal(before);
        Directory.Exists(Path.Combine(_memory.Root, ".git")).Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_ReportDoneFromHomeLaptop_InboxBusReportFileWithOneObservedLine_ArchivedInSameCommitAsAContext()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var job = EnvelopeId.New(Created.AddHours(-1));
        SeededJob report = await _bus.SeedSignedReportAsync(HomeLaptop, Central, job, TestKeys.Acme1, _store, "Root cause is the expired staging certificate\n- files_changed: none\n", created: Created, ct: ct);
        SeededJob context = await _bus.SeedSignedContextAsync(HomeLaptop, Central, TestKeys.Acme1, _store, Fact, created: Created, ct: ct);

        // Act
        PickupResult result = await PassAsync(ct);

        // Assert
        result.Should().Be(new PickupResult(2, 0, 0, 0, new Pushed(1)));
        _memory.InboxText($"bus-report-home-laptop-{job}-{report.Id}-2026-10-29.md").Split("---\n")[2]
            .Should().Be($"- [observed] 2026-10-29 [report {job.Value[..8]}]: Root cause is the expired staging certificate\n");
        (await _bus.ShowBytesAsync(BusPaths.EnvelopeFile(BusPaths.Archive(Acme, 2026, 10), report.Id), ct)).Should().Equal(report.Bytes);
        (await _bus.ShowBytesAsync(BusPaths.EnvelopeFile(BusPaths.Archive(Acme, 2026, 10), context.Id), ct)).Should().Equal(context.Bytes);
        (await _bus.LastCommitSubjectAsync(ct)).Should().Be("acme archive 2 central→archive");
    }

    [Fact]
    public async Task RunAsync_ReportFirstLineSecretShaped_ArchivedOnMain_NoInboxFile_WarningNamesPattern()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        SeededJob report = await _bus.SeedSignedReportAsync(HomeLaptop, Central, EnvelopeId.New(Created), TestKeys.Acme1, _store, "key " + AwsKey + "\n", created: Created, ct: ct);

        // Act
        PickupResult result = await PassAsync(ct);

        // Assert
        result.Should().Be(new PickupResult(0, 1, 1, 0, new Pushed(1)));
        _memory.InboxFiles().Should().BeEmpty();
        (await _bus.ExistsOnMainAsync(BusPaths.EnvelopeFile(BusPaths.Archive(Acme, 2026, 10), report.Id), ct)).Should().BeTrue();
        _log.AtLevel(LogLevel.Warning).Select(e => e.Message).Should().Equal($"pickup: {report.Id}: report line refused (pattern aws-access-key)");
        _log.Messages.Should().NotContain(m => m.Contains(AwsKey, StringComparison.Ordinal));
        SecretSweep.AssertAbsent(_log.Messages);
    }

    [Fact]
    public async Task RunAsync_TwoContextsAndAReportFromWorkLaptop_ThreeInboxFilesOneCommitBodyThreeLines_OneInformationLine()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await _bus.SeedSignedContextAsync(HomeLaptop, Central, TestKeys.Acme1, _store, "- one\n", created: Created, ct: ct);
        await _bus.SeedSignedContextAsync(WorkLaptop, Central, TestKeys.Acme1, _store, "- two\n", created: Created.AddMinutes(1), ct: ct);
        await _bus.SeedSignedReportAsync(WorkLaptop, Central, EnvelopeId.New(Created), TestKeys.Acme1, _store, "Done\n", created: Created.AddMinutes(2), ct: ct);

        // Act
        PickupResult result = await PassAsync(ct);

        // Assert
        result.Should().Be(new PickupResult(3, 0, 0, 0, new Pushed(1)));
        _memory.InboxFiles().Should().HaveCount(3);
        (await _bus.LastCommitSubjectAsync(ct)).Should().Be("acme archive 3 central→archive");
        (await _bus.RunGitAsync(_bus.BareDir, ["log", "-1", "--format=%b", "main"], ct)).Trim().Split('\n').Should().HaveCount(3);
        _log.AtLevel(LogLevel.Information).Where(e => e.Category == typeof(BusPickup).FullName).Select(e => e.Message).Should().Equal(
            "picked up 3 file(s) from 2 machine(s): 3 to inbox, oldest created 2026-10-29T10:42:07Z; archive pushed");
    }

    [Fact]
    public async Task RunAsync_ForeignTenantFieldUnderAcme_LeftOnMainByteIdentical_WarnedOnce_VerifyReportsIt()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        SeededJob context = await _bus.SeedSignedContextAsync(HomeLaptop, Central, TestKeys.Acme1, _store, Fact, created: Created, mutate: b => Replace(b, "tenant: acme", "tenant: other-org"), ct: ct);

        // Act
        PickupResult result = await PassAsync(ct);

        // Assert
        result.Should().Be(new PickupResult(0, 0, 0, 1, null));
        (await _bus.ShowBytesAsync(context.RelativePath, ct)).Should().Equal(context.Bytes);
        _log.AtLevel(LogLevel.Warning).Select(e => e.Message).Should().Equal($"pickup: left {context.RelativePath}: tenant_mismatch (tenant)");
        AuditReport audit = await _services.GetRequiredService<BusAuditor>().RunAsync(null, ct);
        audit.Entries.Should().ContainSingle(e => e.RelativePath == context.RelativePath).Which.Outcome.Should().Be(AuditOutcome.TenantMismatch);
    }

    [Fact]
    public async Task RunAsync_FlippedSignatureByte_LeftOnMain_NoInboxFile_WarnedOnceAcrossTwoPasses()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        SeededJob context = await _bus.SeedSignedContextAsync(HomeLaptop, Central, TestKeys.Acme1, _store, Fact, created: Created, mutate: FlipSig, ct: ct);

        // Act
        await PassAsync(ct);
        PickupResult second = await PassAsync(ct);

        // Assert
        second.Left.Should().Be(1);
        (await _bus.ExistsOnMainAsync(context.RelativePath, ct)).Should().BeTrue();
        _memory.InboxFiles().Should().BeEmpty();
        _log.AtLevel(LogLevel.Warning).Select(e => e.Message).Should().Equal($"pickup: left {context.RelativePath}: invalid_signature (sig)");
    }

    [Fact]
    public async Task RunAsync_SecretShapedLine_DroppedFromInbox_Archived_ValueNowhereInLogsOrInbox()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        SeededJob context = await _bus.SeedSignedContextAsync(HomeLaptop, Central, TestKeys.Acme1, _store, "- one\n- key " + AwsKey + "\n- three\n", created: Created, ct: ct);

        // Act
        PickupResult result = await PassAsync(ct);

        // Assert
        result.Should().Be(new PickupResult(1, 0, 1, 0, new Pushed(1)));
        string inbox = _memory.InboxText($"bus-context-home-laptop-{context.Id}-2026-10-29.md");
        inbox.Split("---\n")[2].Should().Be("- one\n- three\n");
        _log.AtLevel(LogLevel.Warning).Select(e => e.Message).Should().Equal($"pickup: {context.Id}: dropped 1 line (pattern aws-access-key)");
        _log.Messages.Append(inbox).Should().NotContain(m => m.Contains(AwsKey, StringComparison.Ordinal));
        (await _bus.ExistsOnMainAsync(context.RelativePath, ct)).Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_BareRepositoryDeleted_InboxWritten_BusFilesStayInLiveFolders_ErrorOnce()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        SeededJob context = await _bus.SeedSignedContextAsync(HomeLaptop, Central, TestKeys.Acme1, _store, Fact, created: Created, ct: ct);
        await _bus.PullCloneAsync(ct);
        _bus.DeleteBare();
        BusPickup pickup = _services.GetRequiredService<BusPickup>();

        // Act
        PickupResult first = await pickup.RunAsync(ct);
        PickupResult second = await pickup.RunAsync(ct);

        // Assert
        first.Archive.Should().BeOfType<WriteFailed>().Which.Stage.Should().Be("pull");
        second.Archive.Should().BeOfType<WriteFailed>();
        _memory.InboxFiles().Should().Equal($"bus-context-home-laptop-{context.Id}-2026-10-29.md");
        File.Exists(BusPaths.Resolve(_bus.CloneDir, context.RelativePath)).Should().BeTrue();
        _log.AtLevel(LogLevel.Error).Where(e => e.Category == typeof(BusPickup).FullName).Should().ContainSingle()
            .Which.Message.Should().StartWith("pickup: archive failed at pull: ");
        SecretSweep.AssertAbsent(_log.Messages);
    }

    [Fact]
    public async Task RunAsync_EveryPushRefused_DeferredLocalCommit_FilesGoneFromLiveFolders_NextPickupPassPushesBoth()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        SeededJob first = await _bus.SeedSignedContextAsync(HomeLaptop, Central, TestKeys.Acme1, _store, "- one\n", created: Created, ct: ct);
        _hooks.Override = spec => HookedProcessRunner.SubCommand(spec) == "push"
            ? new ProcessResult(1, string.Empty, "! [rejected] main -> main (fetch first)", false, false, false, TimeSpan.Zero)
            : null;
        PickupResult deferred = await PassAsync(ct);
        _hooks.Override = null;
        SeededJob second = await _bus.SeedSignedContextAsync(HomeLaptop, Central, TestKeys.Acme1, _store, "- two\n", created: Created, ct: ct);

        // Act
        PickupResult pushed = await PassAsync(ct);

        // Assert
        deferred.Archive.Should().BeOfType<Deferred>().Which.Attempts.Should().Be(4);
        pushed.Archive.Should().Be(new Pushed(1));
        (await _bus.RevListCountAsync("origin/main..HEAD", ct)).Should().Be(0);
        (await _bus.ExistsOnMainAsync(BusPaths.EnvelopeFile(BusPaths.Archive(Acme, 2026, 10), first.Id), ct)).Should().BeTrue();
        (await _bus.ExistsOnMainAsync(BusPaths.EnvelopeFile(BusPaths.Archive(Acme, 2026, 10), second.Id), ct)).Should().BeTrue();
        (await _bus.ExistsOnMainAsync(first.RelativePath, ct)).Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_CentralsOwnReportOnMain_NeverTouched()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        SeededJob own = await _bus.SeedSignedReportAsync(Central, HomeLaptop, EnvelopeId.New(Created), TestKeys.Acme1, _store, "Done\n", created: Created, ct: ct);
        string head = await _bus.HeadShaAsync(ct);

        // Act
        PickupResult result = await PassAsync(ct);

        // Assert
        result.Should().Be(new PickupResult(0, 0, 0, 0, null));
        (await _bus.ShowBytesAsync(own.RelativePath, ct)).Should().Equal(own.Bytes);
        (await _bus.HeadShaAsync(ct)).Should().Be(head);
    }

    [Fact]
    public async Task RunAsync_AllLogLines_NoBodyNoKey()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await _bus.SeedSignedContextAsync(HomeLaptop, Central, TestKeys.Acme1, _store, Fact, created: Created, ct: ct);
        await _bus.SeedSignedContextAsync(HomeLaptop, Central, TestKeys.Acme1, _store, Fact, created: Created, mutate: FlipSig, ct: ct);

        // Act
        await PassAsync(ct);

        // Assert
        _log.Messages.Should().NotContain(m => m.Contains("staging database", StringComparison.Ordinal));
        SecretSweep.AssertAbsent(_log.Messages);
        SecretSweep.AssertAbsent(_hooks.Calls.SelectMany(SecretSweep.TextsOf));
    }

    private static byte[] Replace(byte[] bytes, string from, string to) =>
        Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace(from, to, StringComparison.Ordinal));

    private static byte[] FlipSig(byte[] bytes)
    {
        string text = Encoding.UTF8.GetString(bytes);
        int at = text.IndexOf("hmac-sha256:", text.IndexOf("\nsig: ", StringComparison.Ordinal), StringComparison.Ordinal) + "hmac-sha256:".Length;
        char c = text[at] == '0' ? '1' : '0';
        return Encoding.UTF8.GetBytes(text[..at] + c + text[(at + 1)..]);
    }

    /// <summary>The listener's order: pull the clone, then one pickup pass.</summary>
    private async Task<PickupResult> PassAsync(CancellationToken ct)
    {
        await _bus.PullCloneAsync(ct);
        return await _services.GetRequiredService<BusPickup>().RunAsync(ct);
    }
}
