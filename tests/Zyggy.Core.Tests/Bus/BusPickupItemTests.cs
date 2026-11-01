using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Git;
using Zyggy.Core.Memory;
using Zyggy.Core.Processes;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Bus;

/// <summary>
/// Spec 41 AC-28 and AC-33..AC-41 through the seams (plan 41 Step 9): Central's pickup settles a <c>via: bus</c> item in one pass —
/// filed through the one archive writer with a signed <c>done</c> report, refused with <c>failed</c> / <c>archive_refused</c>, left, or
/// waiting — the <c>items/</c> file removed and the report added in the pass's single bus commit. Bus git and memory git are recorded
/// by one runner and told apart by their working directory.
/// </summary>
public sealed class BusPickupItemTests : IDisposable
{
    private const string Fact = "- [stated] 2026-10-31 (project:calizr): The staging database is restored from Sunday's snapshot";

    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private const string Rejected = " ! [rejected]        HEAD -> main (fetch first)\nerror: failed to push some refs";

    private const string Locked = "fatal: Unable to create 'memory/.git/index.lock': File exists.";

    private const string AwsKey = "AKIAABCDEFGHIJKLMNOP";

    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName Central = MachineName.Central;

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly MachineName WorkLaptop = MachineName.Parse("work-laptop");

    private static readonly BusRemote Remote = new("acme-org", "acme-bus", "main", new Uri("https://example.invalid/acme-org/acme-bus.git"), null);

    private static readonly DateTimeOffset T = new(2026, 10, 31, 14, 5, 0, TimeSpan.Zero);

    private static readonly byte[] Notes = Encoding.UTF8.GetBytes("# Restore\n\nSteps for the staging restore.\n");

    private static readonly SecretPatterns Patterns = SecretPatterns.Load(Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt")).Patterns!;

    private readonly TempCheckout _checkout = new(Acme, Central);

    private readonly MemoryTree _memory = new(("profile.md", "---\nname: profile\n---\n"));

    private readonly RecordingProcessRunner _processes = new();

    private readonly IBusProvider _provider = Substitute.For<IBusProvider>();

    private readonly RecordingLogger<BusPickup> _log = new();

    private readonly FakeTimeProvider _clock = new(T);

    private readonly RecordingEnvironment _environment = new(new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        ["HOME"] = Path.GetTempPath(),
        ["ZYGGY_HOOKS"] = "off",
    });

    private ISecretStore? _store;

    public BusPickupItemTests()
    {
        _provider.Remote.Returns(Remote);
        _provider.PullAsync(Arg.Any<CancellationToken>()).Returns(new BusSyncResult(true, string.Empty));
        _provider.PushAsync(Arg.Any<CancellationToken>()).Returns(new PushSucceeded());
    }

    public static TheoryData<string, string> CentralRefusals() => new()
    {
        { "type_not_allowed", "type_not_allowed (text/markdown)" },
        { "project_cap", "project_cap" },
        { "slug_taken", "slug_taken" },
        { "secret_line", "secret_pattern aws-access-key (line 2)" },
        { "contact_detail", "contact_detail (name)" },
        { "office_refused", "office_refused (macro_enabled)" },
    };

    public void Dispose()
    {
        _checkout.Dispose();
        _memory.Dispose();
    }

    [Fact]
    public async Task RunAsync_BusItem_FiledThroughAddBytes_DoneReportToSenderInReplyToItem_ItemRemovedInPassCommit()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        (string path, EnvelopeId id) = await DropItemAsync(HomeLaptop, Notes, ct);

        // Act
        PickupResult result = await (await PickupAsync(ct)).RunAsync(ct);

        // Assert
        result.ItemsFiled.Should().Be(1);
        result.Archive.Should().Be(new Pushed(1));
        File.ReadAllBytes(_memory.Full("archive/calizr/staging-restore-notes.txt")).Should().Equal(Notes);
        ArchiveSidecar.TryParse(MemoryFileReader.Read(_memory.Full("archive/calizr/staging-restore-notes.md")))!.SourceName.Should().Be("restore-notes.md");
        int memoryCommit = _processes.Calls.FindIndex(c => c.WorkingDirectory == _memory.Root && RecordingProcessRunner.SubCommand(c) == "commit");
        int busCommit = _processes.Calls.FindIndex(c => c.WorkingDirectory == _checkout.Root && RecordingProcessRunner.SubCommand(c) == "commit");
        memoryCommit.Should().BeGreaterThan(-1).And.BeLessThan(busCommit);
        _processes.Calls[memoryCommit].StandardInput.Should().StartWith("archive add calizr/staging-restore-notes.txt\n\nmedia: text/markdown;");
        _processes.CallsOf("rm").Should().ContainSingle().Which.Arguments.Should().EndWith(path);
        Model report = await SingleReportAsync(ct);
        report.Header.From.Should().Be(Central);
        report.Header.To.Should().Be(HomeLaptop);
        report.Header.InReplyTo.Should().Be(id);
        report.Report!.Status.Should().Be(ReportStatus.Done);
        report.Report.Reason.Should().BeNull();
        report.BodyText.Should().StartWith("archived: archive/calizr/staging-restore-notes.txt\nsidecar: archive/calizr/staging-restore-notes.md\n- [stated] 2026-10-31 (project:calizr): Archived \"Staging restore notes\"")
            .And.EndWith($"\ncommit: {Sha} pushed\n");
        BusCommit().StandardInput.Should().Be($"acme archive 1 central→archive\n\n{path} → filed archive/calizr/staging-restore-notes.txt");
        _log.AtLevel(LogLevel.Information).Should().ContainSingle().Which.Message.Should().EndWith("; items filed 1, refused 0");
    }

    [Fact]
    public async Task RunAsync_TwoItemsSameBytes_OneArchivedItemTwoDoneReports_SecondAlreadyFiled()
    {
        // Arrange: the laptop re-ran after exit 7 (committed locally, pushed later): two items/ envelopes carry the same file.
        var ct = TestContext.Current.CancellationToken;
        (string first, EnvelopeId firstId) = await DropItemAsync(HomeLaptop, Notes, ct);
        (string second, EnvelopeId secondId) = await DropItemAsync(HomeLaptop, Notes, ct);

        // Act
        PickupResult result = await (await PickupAsync(ct)).RunAsync(ct);

        // Assert
        result.ItemsFiled.Should().Be(2);
        _processes.Calls.Count(c => c.WorkingDirectory == _memory.Root && RecordingProcessRunner.SubCommand(c) == "commit").Should().Be(1);
        Directory.EnumerateFiles(_memory.Full("archive/calizr")).Select(Path.GetFileName).Should().BeEquivalentTo("staging-restore-notes.txt", "staging-restore-notes.md");
        File.ReadAllLines(_memory.Full("inbox/remember-2026-10-31.md")).Count(l => l.Contains("Archived \"Staging restore notes\"", StringComparison.Ordinal)).Should().Be(1);
        IReadOnlyList<Model> reports = await ReportsAsync(ct);
        reports.Select(r => r.Header.InReplyTo).Should().BeEquivalentTo([firstId, secondId]);
        reports.Should().OnlyContain(r => r.Report!.Status == ReportStatus.Done);
        reports.Single(r => r.Header.InReplyTo == secondId).BodyText.Should().Contain("already filed\n");
        BusCommit().StandardInput.Should().Be(
            "acme archive 2 central→archive\n\n"
            + $"{first} → filed archive/calizr/staging-restore-notes.txt\n{second} → filed archive/calizr/staging-restore-notes.txt");
    }

    [Theory]
    [MemberData(nameof(CentralRefusals))]
    public async Task RunAsync_RefusedOnCentral_FailedArchiveRefused_FirstBodyLine_WarningOncePerId_ItemRemoved(string scenario, string rendered)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var options = new ArchiveOptions();
        byte[] file = Notes;
        string name = "Staging restore notes";
        string source = "restore-notes.md";
        switch (scenario)
        {
            case "type_not_allowed":
                options = options with { AllowedTypes = new HashSet<ArchiveMediaType> { ArchiveMediaType.Pdf } };
                break;
            case "project_cap":
                _memory.Write("archive/calizr/old.txt", "Old staging notes from last year, kept for reference.\n");
                options = options with { ItemMaxBytes = 60, ProjectMaxBytes = 60, TotalMaxBytes = 1000 };
                break;
            case "slug_taken":
                _memory.Write("archive/calizr/staging-restore-notes.txt", "Another file.\n");
                break;
            case "secret_line":
                file = Encoding.UTF8.GetBytes($"Restore notes.\nkey {AwsKey}\n");
                break;
            case "contact_detail":
                name = "Notes from marie@example.com";
                break;
            default:
                file = await File.ReadAllBytesAsync(Path.Combine(Golden.Directory, "office", "sample.docm"), ct);
                source = "sample.docm";
                break;
        }

        (string path, EnvelopeId id) = await DropItemAsync(HomeLaptop, file, ct, name: name, sourceName: source);
        BusPickup pickup = await PickupAsync(ct, options: options);
        var memoryBefore = TreeFingerprint.Of(_memory.Root);

        // Act
        PickupResult result = await pickup.RunAsync(ct);

        // Assert
        result.ItemsRefused.Should().Be(1);
        TreeFingerprint.Of(_memory.Root).Should().Equal(memoryBefore);
        _processes.Calls.Where(c => c.WorkingDirectory == _memory.Root).Select(RecordingProcessRunner.SubCommand).Should().NotContain(["add", "commit", "push"]);
        _processes.CallsOf("rm").Should().ContainSingle().Which.Arguments.Should().EndWith(path);
        Model report = await SingleReportAsync(ct);
        report.Report!.Status.Should().Be(ReportStatus.Failed);
        report.Report.Reason.Should().Be("archive_refused");
        report.BodyText.Should().Be($"archive: refused: {rendered}\n");
        _log.AtLevel(LogLevel.Warning).Should().ContainSingle(e => e.Message.StartsWith($"pickup: item {id} refused: ", StringComparison.Ordinal));
        _log.Messages.Should().NotContain(m => m.Contains(AwsKey, StringComparison.Ordinal));
        BusCommit().StandardInput.Should().EndWith($"{path} → refused {rendered.Split(' ')[0]}");
    }

    [Fact]
    public async Task RunAsync_BusItemSizeOverCentralCap_TooLargeBeforeDecoding()
    {
        // Arrange: Central's item_max_bytes is tighter than the laptop's; the signed size decides before the body is decoded again.
        var ct = TestContext.Current.CancellationToken;
        await DropItemAsync(HomeLaptop, Notes, ct);
        BusPickup pickup = await PickupAsync(ct, options: new ArchiveOptions { ItemMaxBytes = 10, ProjectMaxBytes = 10, TotalMaxBytes = 10 });

        // Act
        PickupResult result = await pickup.RunAsync(ct);

        // Assert
        result.ItemsRefused.Should().Be(1);
        (await SingleReportAsync(ct)).BodyText.Should().Be($"archive: refused: too_large ({Notes.Length} > 10)\n");
        _processes.Calls.Where(c => c.WorkingDirectory == _memory.Root).Should().BeEmpty();
    }

    [Theory]
    [InlineData("tampered", "invalid_signature", "sig")]
    [InlineData("wrong_tenant", "tenant_mismatch", "tenant")]
    [InlineData("from_not_folder", "writer_mismatch", "from")]
    [InlineData("to_not_central", "wrong_recipient", "to")]
    [InlineData("unknown_key", "unknown_key", "key_id")]
    public async Task RunAsync_Tampered_WrongTenant_FromNotFolder_ToNotCentral_UnknownKey_Left_NoReport_MemoryUntouched_WarningOnce(string scenario, string reason, string field)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string path = scenario switch
        {
            "tampered" => (await DropItemAsync(HomeLaptop, Notes, ct, mutate: FlipSig)).Path,
            "wrong_tenant" => (await DropItemAsync(HomeLaptop, Notes, ct, mutate: b => Replace(b, "tenant: acme", "tenant: other-org"))).Path,
            "from_not_folder" => (await DropItemAsync(WorkLaptop, Notes, ct, folder: HomeLaptop)).Path,
            "to_not_central" => (await DropItemAsync(HomeLaptop, Notes, ct, to: WorkLaptop)).Path,
            _ => (await DropItemAsync(HomeLaptop, Notes, ct, signWith: TestKeys.Acme2)).Path,
        };
        BusPickup pickup = await PickupAsync(ct);
        var memoryBefore = TreeFingerprint.Of(_memory.Root);

        // Act
        PickupResult first = await pickup.RunAsync(ct);
        await pickup.RunAsync(ct);

        // Assert
        first.Left.Should().Be(1);
        _checkout.Exists(path).Should().BeTrue();
        _checkout.FilesUnder(BusPaths.Reports(Acme, Central)).Should().BeEmpty();
        _processes.CallsOf("commit").Should().BeEmpty();
        _processes.Calls.Where(c => c.WorkingDirectory == _memory.Root).Should().BeEmpty();
        TreeFingerprint.Of(_memory.Root).Should().Equal(memoryBefore);
        _log.AtLevel(LogLevel.Warning).Should().ContainSingle().Which.Message.Should().Be($"pickup: left {path}: {reason} ({field})");
    }

    [Theory]
    [InlineData("not_on_branch")]
    [InlineData("operation_in_progress")]
    [InlineData("index_lock")]
    [InlineData("io_error")]
    [InlineData("archive_configuration_missing")]
    public async Task RunAsync_MemoryNotOnBranch_OperationInProgress_IndexLock_IoError_ArchiveConfigMissing_ItemWaits_NoReport_ContextsStillPickedUp(string cause)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        (string item, _) = await DropItemAsync(HomeLaptop, Notes, ct);
        (string context, _) = await DropContextAsync(HomeLaptop, Fact + "\n", ct);
        var locked = RecordingProcessRunner.Fail(128, Locked);
        _processes.Hook = spec => spec.WorkingDirectory != _memory.Root ? null : (cause, RecordingProcessRunner.SubCommand(spec)) switch
        {
            ("not_on_branch", "symbolic-ref") => RecordingProcessRunner.Fail(1, ""),
            ("operation_in_progress", "rev-parse") when spec.Arguments.Contains("--git-path") => RecordingProcessRunner.Ok(".git/rebase-merge\n.git/rebase-apply\n.git/MERGE_HEAD\n"),
            ("index_lock", "commit") => locked,
            _ => null,
        };
        if (cause == "operation_in_progress")
        {
            Directory.CreateDirectory(Path.Combine(_memory.Root, ".git", "rebase-merge"));
        }

        if (cause == "io_error")
        {
            _memory.Write("archive/calizr", "a file where the project folder should be\n");
        }

        BusPickup pickup = await PickupAsync(ct, archive: cause != "archive_configuration_missing");

        // Act
        PickupResult result = await RunAdvancingClockAsync(pickup.RunAsync(ct), ct);

        // Assert
        result.ItemsWaiting.Should().Be(1);
        result.Inboxed.Should().Be(1);
        _checkout.Exists(item).Should().BeTrue();
        _processes.CallsOf("rm").Should().BeEmpty();
        _checkout.FilesUnder(BusPaths.Reports(Acme, Central)).Should().BeEmpty();
        BusCommit().StandardInput.Should().StartWith("acme archive 1 central→archive\n\n" + context + " → ").And.NotContain("items/");
        _log.AtLevel(LogLevel.Information).Should().ContainSingle().Which.Message.Should().NotContain("items filed");
    }

    [Fact]
    public async Task RunAsync_ArchiveConfigMissing_ErrorOnceAcrossPasses()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropItemAsync(HomeLaptop, Notes, ct);
        BusPickup pickup = await PickupAsync(ct, archive: false);

        // Act
        await pickup.RunAsync(ct);
        await pickup.RunAsync(ct);

        // Assert
        _log.AtLevel(LogLevel.Error).Should().ContainSingle().Which.Message.Should().Be("pickup: archive configuration missing: archive configuration missing (ZYGGY_INSTANCE_DIR not set)");
    }

    [Fact]
    public async Task RunAsync_AlreadyFiled_DoneReport_NoSecondItem()
    {
        // Arrange: a sidecar at the slug with the item's SHA-256, committed (git status clean).
        var ct = TestContext.Current.CancellationToken;
        _memory.Write("archive/calizr/staging-restore-notes.txt", Encoding.UTF8.GetString(Notes));
        _memory.Write("archive/calizr/staging-restore-notes.md", MemoryFileWriter.Render(new ArchiveSidecar(
            "Staging restore notes", "How the staging database was restored", Slug.Parse("calizr"), ArchiveMediaType.TextMarkdown, Notes.Length,
            Convert.ToHexStringLower(SHA256.HashData(Notes)), new DateOnly(2026, 10, 30), "restore-notes.md", new DateOnly(2026, 10, 30)).ToMemoryFile()));
        (string path, _) = await DropItemAsync(HomeLaptop, Notes, ct);
        var memoryBefore = TreeFingerprint.Of(_memory.Root);

        // Act
        PickupResult result = await (await PickupAsync(ct)).RunAsync(ct);

        // Assert
        result.ItemsFiled.Should().Be(1);
        TreeFingerprint.Of(_memory.Root).Should().Equal(memoryBefore);
        _processes.Calls.Where(c => c.WorkingDirectory == _memory.Root).Select(RecordingProcessRunner.SubCommand).Should().NotContain("commit");
        (await SingleReportAsync(ct)).Report!.Status.Should().Be(ReportStatus.Done);
        _processes.CallsOf("rm").Should().ContainSingle().Which.Arguments.Should().EndWith(path);
    }

    [Fact]
    public async Task RunAsync_BusCommitFailedAfterFiling_NextPassAlreadyFiled_DoneReportOnce()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        (string path, EnvelopeId id) = await DropItemAsync(HomeLaptop, Notes, ct);
        BusPickup pickup = await PickupAsync(ct);
        _processes.Hook = spec => spec.WorkingDirectory == _checkout.Root && RecordingProcessRunner.SubCommand(spec) == "commit" ? RecordingProcessRunner.Fail(1, "fatal: simulated") : null;
        PickupResult failed = await pickup.RunAsync(ct);
        foreach (string stray in _checkout.FilesUnder(BusPaths.Reports(Acme, Central)))
        {
            File.Delete(BusPaths.Resolve(_checkout.Root, stray));
        }

        _processes.Hook = null;

        // Act
        PickupResult result = await pickup.RunAsync(ct);

        // Assert
        failed.Archive.Should().BeOfType<WriteFailed>();
        result.ItemsFiled.Should().Be(1);
        _processes.Calls.Count(c => c.WorkingDirectory == _memory.Root && RecordingProcessRunner.SubCommand(c) == "commit").Should().Be(1);
        File.ReadAllLines(_memory.Full("inbox/remember-2026-10-31.md")).Count(l => l.Contains("Archived \"Staging restore notes\"", StringComparison.Ordinal)).Should().Be(1);
        Model report = await SingleReportAsync(ct);
        report.Header.InReplyTo.Should().Be(id);
        report.BodyText.Should().Contain("already filed\n");
        _processes.CallsOf("commit").Last(c => c.WorkingDirectory == _checkout.Root).StandardInput.Should().EndWith($"{path} → filed archive/calizr/staging-restore-notes.txt");
    }

    [Fact]
    public async Task RunAsync_MemoryPushDeferred_FiledRemoved_ReportEndsPushDeferred_WarningOnce()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        (string path, EnvelopeId id) = await DropItemAsync(HomeLaptop, Notes, ct);
        _processes.On("push", RecordingProcessRunner.Fail(1, Rejected), RecordingProcessRunner.Fail(1, Rejected));

        // Act
        PickupResult result = await (await PickupAsync(ct)).RunAsync(ct);

        // Assert
        result.ItemsFiled.Should().Be(1);
        _processes.CallsOf("rm").Should().ContainSingle().Which.Arguments.Should().EndWith(path);
        (await SingleReportAsync(ct)).BodyText.Should().EndWith($"commit: {Sha} push deferred\n");
        _log.AtLevel(LogLevel.Warning).Should().ContainSingle().Which.Message.Should().Be($"pickup: item {id} filed; memory push deferred");
    }

    [Fact]
    public async Task RunAsync_ExceptionInItemPath_ErrorNamesType_ItemStays_NoModelCall_HooksVariableNeverRead()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        (string path, EnvelopeId id) = await DropItemAsync(HomeLaptop, Notes, ct);
        _processes.Hook = spec => spec.WorkingDirectory == _memory.Root && RecordingProcessRunner.SubCommand(spec) == "symbolic-ref"
            ? throw new InvalidOperationException("simulated")
            : null;
        BusPickup pickup = await PickupAsync(ct);

        // Act
        PickupResult first = await pickup.RunAsync(ct);
        await pickup.RunAsync(ct);

        // Assert
        first.ItemsWaiting.Should().Be(1);
        _checkout.Exists(path).Should().BeTrue();
        _checkout.FilesUnder(BusPaths.Reports(Acme, Central)).Should().BeEmpty();
        _log.AtLevel(LogLevel.Error).Should().ContainSingle().Which.Message.Should().Be($"pickup: item {id} failed: InvalidOperationException");
        _processes.Calls.Should().OnlyContain(c => !c.FileName.Contains("claude", StringComparison.OrdinalIgnoreCase));
        _environment.Read.Should().NotContain("ZYGGY_HOOKS");
    }

    [Fact]
    public async Task RunAsync_ReportKeyAbsent_ItemWaits_ErrorOnce()
    {
        // Arrange: the node signs with acme/2, which the store does not hold.
        var ct = TestContext.Current.CancellationToken;
        (string path, _) = await DropItemAsync(HomeLaptop, Notes, ct);
        BusPickup pickup = await PickupAsync(ct, signWith: TestKeys.Acme2);

        // Act
        PickupResult first = await pickup.RunAsync(ct);
        await pickup.RunAsync(ct);

        // Assert
        first.ItemsWaiting.Should().Be(1);
        _checkout.Exists(path).Should().BeTrue();
        _checkout.FilesUnder(BusPaths.Reports(Acme, Central)).Should().BeEmpty();
        _processes.CallsOf("commit").Where(c => c.WorkingDirectory == _checkout.Root).Should().BeEmpty();
        _log.AtLevel(LogLevel.Error).Should().ContainSingle().Which.Message.Should().StartWith("pickup: cannot sign reports: ");
    }

    [Fact]
    public async Task RunAsync_OneDriveItem_WaitsWithErrorOnceNotConfigured_BusItemsAndContextsUnaffected()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string oneDrive = await DropOneDriveItemAsync(WorkLaptop, ct);
        (string busItem, _) = await DropItemAsync(HomeLaptop, Notes, ct);
        (string context, _) = await DropContextAsync(HomeLaptop, Fact + "\n", ct);
        BusPickup pickup = await PickupAsync(ct);

        // Act
        PickupResult first = await pickup.RunAsync(ct);
        await pickup.RunAsync(ct);

        // Assert
        first.ItemsWaiting.Should().Be(1);
        first.ItemsFiled.Should().Be(1);
        first.Inboxed.Should().Be(1);
        _checkout.Exists(oneDrive).Should().BeTrue();
        _processes.CallsOf("rm").Should().NotBeEmpty().And.OnlyContain(c => c.Arguments.Contains(busItem));
        _processes.CallsOf("mv").Should().NotBeEmpty().And.OnlyContain(c => c.Arguments.Contains(context));
        _log.AtLevel(LogLevel.Error).Should().ContainSingle().Which.Message.Should().StartWith("pickup: OneDrive fetch not configured");
    }

    [Fact]
    public async Task RunAsync_OnlyContexts_MemoryGitNeverRun()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropContextAsync(HomeLaptop, Fact + "\n", ct);
        await DropReportAsync(HomeLaptop, ct);

        // Act
        await (await PickupAsync(ct)).RunAsync(ct);

        // Assert
        _processes.Calls.Should().NotBeEmpty().And.OnlyContain(c => c.WorkingDirectory == _checkout.Root);
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

    private async Task<PickupResult> RunAdvancingClockAsync(Task<PickupResult> task, CancellationToken ct)
    {
        while (!task.IsCompleted)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(1, ct);
        }

        return await task;
    }

    private ProcessSpec BusCommit() =>
        _processes.CallsOf("commit").Should().ContainSingle(c => c.WorkingDirectory == _checkout.Root).Subject;

    private async Task<IReadOnlyList<Model>> ReportsAsync(CancellationToken ct)
    {
        var signer = new EnvelopeSigner(await StoreAsync(ct));
        var reports = new List<Model>();
        foreach (string path in _checkout.FilesUnder(BusPaths.Reports(Acme, Central)))
        {
            EnvelopeResult verified = await signer.ParseAndVerifyAsync(_checkout.Read(path), Acme, ct);
            verified.IsAccepted.Should().BeTrue(verified.Rejection?.ToString());
            reports.Add(verified.Envelope!);
        }

        return reports;
    }

    private async Task<Model> SingleReportAsync(CancellationToken ct) => (await ReportsAsync(ct)).Should().ContainSingle().Subject;

    private async Task<ISecretStore> StoreAsync(CancellationToken ct) => _store ??= await TestKeys.StoreWithAsync(TestKeys.Acme1, ct);

    private async Task<(string Path, EnvelopeId Id)> DropItemAsync(
        MachineName from,
        byte[] file,
        CancellationToken ct,
        MachineName? to = null,
        MachineName? folder = null,
        KeyId? signWith = null,
        string name = "Staging restore notes",
        string sourceName = "restore-notes.md",
        Func<byte[], byte[]>? mutate = null)
    {
        var id = EnvelopeId.New(_clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        ISecretStore store = signWith is null ? await StoreAsync(ct) : await TestKeys.StoreWithAsync(signWith, ct);
        byte[] bytes = await TestEnvelopes.SignedItemBytesAsync(from, to ?? Central, store, signWith ?? TestKeys.Acme1, file, T, ct, name: name, sourceName: sourceName, id: id, mutate: mutate);
        string path = BusPaths.EnvelopeFile(BusPaths.Items(Acme, folder ?? from), id);
        _checkout.Drop(path, bytes);
        return (path, id);
    }

    private async Task<string> DropOneDriveItemAsync(MachineName from, CancellationToken ct)
    {
        var id = EnvelopeId.New(_clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        byte[] bytes = await TestEnvelopes.SignedOneDriveItemBytesAsync(from, await StoreAsync(ct), TestKeys.Acme1, Notes, T, ct, id);
        string path = BusPaths.EnvelopeFile(BusPaths.Items(Acme, from), id);
        _checkout.Drop(path, bytes);
        return path;
    }

    private async Task<(string Path, EnvelopeId Id)> DropContextAsync(MachineName from, string body, CancellationToken ct)
    {
        var id = EnvelopeId.New(_clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        byte[] bytes = await TestEnvelopes.SignedContextBytesAsync(from, Central, await StoreAsync(ct), TestKeys.Acme1, body, T, ct, id: id);
        string path = BusPaths.EnvelopeFile(BusPaths.Context(Acme, from), id);
        _checkout.Drop(path, bytes);
        return (path, id);
    }

    private async Task DropReportAsync(MachineName from, CancellationToken ct)
    {
        var id = EnvelopeId.New(_clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        byte[] bytes = await TestEnvelopes.SignedReportBytesAsync(from, Central, EnvelopeId.New(T), await StoreAsync(ct), TestKeys.Acme1, "Done\n", ct, id: id, created: T);
        _checkout.Drop(BusPaths.EnvelopeFile(BusPaths.Reports(Acme, from), id), bytes);
    }

    private async Task<BusPickup> PickupAsync(CancellationToken ct, ArchiveOptions? options = null, bool archive = true, KeyId? signWith = null)
    {
        ISecretStore store = await StoreAsync(ct);
        var git = new GitClient(_processes, new GitClientOptions { Identity = TestGitIdentities.Central }, _clock);
        var signer = new EnvelopeSigner(store);
        var bus = new BusRepository(git, _provider, new BusIdentity(Acme, Central), _checkout.Root, signer, _clock, () => 0.5, new RecordingLogger<BusRepository>());
        ItemFiler? filer = null;
        if (archive)
        {
            options ??= new ArchiveOptions();
            var publisher = new MemoryPublisher(new GitClient(_processes, new GitClientOptions(), _clock));
            var service = new ArchiveService(_memory.Paths, TimeZoneInfo.Utc, _clock, Patterns, options, SourceDenyList.Build(_environment, options, _memory.Paths), publisher, _memory.Root);
            filer = new ItemFiler(service, options);
        }

        return new BusPickup(
            bus,
            signer,
            new PickupInbox(_memory.Paths, TimeZoneInfo.Utc, _clock),
            Patterns,
            _log,
            signWith ?? TestKeys.Acme1,
            filer,
            archive ? null : "archive configuration missing (ZYGGY_INSTANCE_DIR not set)");
    }

    /// <summary>An environment that records every key looked up; enumerating it counts as reading every key.</summary>
    private sealed class RecordingEnvironment(IReadOnlyDictionary<string, string?> inner) : IReadOnlyDictionary<string, string?>
    {
        public HashSet<string> Read { get; } = new(StringComparer.Ordinal);

        public IEnumerable<string> Keys => Touch(inner.Keys);

        public IEnumerable<string?> Values => Touch(inner.Keys).Select(k => inner[k]);

        public int Count => inner.Count;

        public string? this[string key] => Note(key, inner[key]);

        public bool ContainsKey(string key) => Note(key, inner.ContainsKey(key));

        public bool TryGetValue(string key, [MaybeNullWhen(false)] out string? value)
        {
            Read.Add(key);
            return inner.TryGetValue(key, out value);
        }

        public IEnumerator<KeyValuePair<string, string?>> GetEnumerator()
        {
            Touch(inner.Keys);
            return inner.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private TValue Note<TValue>(string key, TValue value)
        {
            Read.Add(key);
            return value;
        }

        private List<string> Touch(IEnumerable<string> keys)
        {
            var list = keys.ToList();
            Read.UnionWith(list);
            return list;
        }
    }
}
