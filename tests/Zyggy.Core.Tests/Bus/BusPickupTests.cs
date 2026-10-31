using System.Text;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Git;
using Zyggy.Core.Memory;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Hub;
using Zyggy.Core.Tests.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Bus;

/// <summary>Spec 13 AC-1..AC-13, AC-17..AC-19: one pickup pass on Central, through the git and bus seams.</summary>
public sealed class BusPickupTests : IDisposable
{
    private const string Fact = "- [stated] 2026-10-29 (project:calizr): The staging database is restored from Sunday's snapshot";

    private const string AwsKey = "AKIAABCDEFGHIJKLMNOP";

    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName Central = MachineName.Central;

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly MachineName WorkLaptop = MachineName.Parse("work-laptop");

    private static readonly BusRemote Remote = new("acme-org", "acme-bus", "main", new Uri("https://example.invalid/acme-org/acme-bus.git"), null);

    private static readonly DateTimeOffset T = new(2026, 10, 29, 10, 42, 7, TimeSpan.Zero);

    private readonly TempCheckout _checkout = new(Acme, Central);

    private readonly MemoryTree _memory = new(("profile.md", "---\nname: profile\n---\n"));

    private readonly RecordingProcessRunner _processes = new();

    private readonly IBusProvider _provider = Substitute.For<IBusProvider>();

    private readonly RecordingLogger<BusPickup> _log = new();

    private readonly FakeTimeProvider _clock = new(T);

    private ISecretStore? _store;

    public BusPickupTests()
    {
        _provider.Remote.Returns(Remote);
        _provider.PullAsync(Arg.Any<CancellationToken>()).Returns(new BusSyncResult(true, string.Empty));
        _provider.PushAsync(Arg.Any<CancellationToken>()).Returns(new PushSucceeded());
    }

    public void Dispose()
    {
        _checkout.Dispose();
        _memory.Dispose();
    }

    [Fact]
    public async Task RunAsync_ContextToCentral_InboxFileThenOneArchiveCommit_InformationLineOnce()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        (string path, EnvelopeId id) = await DropContextAsync(HomeLaptop, Fact + "\n", ct);

        // Act
        PickupResult result = await (await PickupAsync(ct)).RunAsync(ct);

        // Assert
        result.Should().Be(new PickupResult(1, 0, 0, 0, new Pushed(1)));
        InboxBody($"bus-context-home-laptop-{id}-2026-10-29.md").Should().Be(Fact + "\n");
        _processes.CallsOf("mv").Should().ContainSingle().Which.Arguments.Should().Equal(
            "mv", "--", path, BusPaths.EnvelopeFile(BusPaths.Archive(Acme, 2026, 10), id));
        _processes.CallsOf("commit").Should().ContainSingle().Which.StandardInput.Should().StartWith("acme archive 1 central→archive");
        _log.AtLevel(LogLevel.Information).Select(e => e.Message).Should().Equal(
            "picked up 1 file(s) from 1 machine(s): 1 to inbox, oldest created 2026-10-29T10:42:07Z; archive pushed");
    }

    [Theory]
    [InlineData(ReportStatus.Done, "\n  ## REPORT\nRoot cause is the expired staging certificate\n- files_changed: none\n", "## REPORT")]
    [InlineData(ReportStatus.Failed, "Root cause is the expired staging certificate\n- files_changed: none\n", "Root cause is the expired staging certificate")]
    [InlineData(ReportStatus.Timeout, "Root cause is the expired staging certificate\n", "Root cause is the expired staging certificate")]
    public async Task RunAsync_ReportDoneFailedOrTimeout_InboxFileHoldsExactlyOneObservedLine(ReportStatus status, string body, string expected)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var job = EnvelopeId.New(T.AddHours(-1));
        (_, EnvelopeId id) = await DropReportAsync(HomeLaptop, job, body, ct, status, status == ReportStatus.Done ? null : "claude_error");

        // Act
        PickupResult result = await (await PickupAsync(ct)).RunAsync(ct);

        // Assert
        result.Inboxed.Should().Be(1);
        InboxBody($"bus-report-home-laptop-{job}-{id}-2026-10-29.md").Should().Be($"- [observed] 2026-10-29 [report {job.Value[..8]}]: {expected}\n");
    }

    [Fact]
    public async Task ReportLine_FirstNonEmptyLineTrimmed_DateIsCreatedUtc_SourceIsReportJob8()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var job = EnvelopeId.New(T.AddHours(-1));
        var created = new DateTimeOffset(2026, 10, 30, 1, 30, 0, TimeSpan.FromHours(2));
        Model report = await TestEnvelopes.SignedReportAsync(HomeLaptop, Central, job, await StoreAsync(ct), TestKeys.Acme1, "\r\n   \r\n\t  Root cause  is here \r\nsecond\r\n", ct, created: created);

        // Act
        string? line = BusPickup.ReportLine(report);

        // Assert
        line.Should().Be($"- [observed] 2026-10-29 [report {job.Value[..8]}]: Root cause  is here");
    }

    [Fact]
    public async Task RunAsync_ReportFirstLineSecretShaped_ArchivedNoInboxFile_OneWarningNamesPattern_LineNowhere()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        (_, EnvelopeId id) = await DropReportAsync(HomeLaptop, EnvelopeId.New(T), "key " + AwsKey + "\nrest\n", ct);

        // Act
        PickupResult result = await (await PickupAsync(ct)).RunAsync(ct);

        // Assert
        result.Should().Be(new PickupResult(0, 1, 1, 0, new Pushed(1)));
        InboxFiles().Should().BeEmpty();
        _log.AtLevel(LogLevel.Warning).Select(e => e.Message).Should().Equal($"pickup: {id}: report line refused (pattern aws-access-key)");
        _log.Messages.Should().NotContain(m => m.Contains(AwsKey, StringComparison.Ordinal));
        _processes.CallsOf("mv").Should().ContainSingle();
        SecretSweep.AssertAbsent(_log.Messages);
    }

    [Fact]
    public async Task RunAsync_ReportSecretOnALaterLine_NotCopiedAnyway_InboxHoldsFirstLineOnly()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var job = EnvelopeId.New(T);
        (_, EnvelopeId id) = await DropReportAsync(HomeLaptop, job, "Deployed\nnotes\nkey " + AwsKey + "\n", ct);

        // Act
        await (await PickupAsync(ct)).RunAsync(ct);

        // Assert
        InboxBody($"bus-report-home-laptop-{job}-{id}-2026-10-29.md").Should().Be($"- [observed] 2026-10-29 [report {job.Value[..8]}]: Deployed\n");
        _log.AtLevel(LogLevel.Warning).Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_ReportBodyBlankOnly_ArchivedNoInboxFileNoWarning()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropReportAsync(HomeLaptop, EnvelopeId.New(T), "\n   \n", ct);

        // Act
        PickupResult result = await (await PickupAsync(ct)).RunAsync(ct);

        // Assert
        result.Should().Be(new PickupResult(0, 1, 0, 0, new Pushed(1)));
        InboxFiles().Should().BeEmpty();
        _log.AtLevel(LogLevel.Warning).Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_ReportRejected_ArchivedNoInboxFile()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropReportAsync(HomeLaptop, EnvelopeId.New(T), "rejected: unknown_project\n", ct, ReportStatus.Rejected, "unknown_project");

        // Act
        PickupResult result = await (await PickupAsync(ct)).RunAsync(ct);

        // Assert
        result.Should().Be(new PickupResult(0, 1, 0, 0, new Pushed(1)));
        InboxFiles().Should().BeEmpty();
        _processes.CallsOf("mv").Should().ContainSingle();
    }

    [Fact]
    public async Task RunAsync_ToIsNotCentral_NotCopiedNotMovedNoLogLine()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropContextAsync(HomeLaptop, Fact + "\n", ct, to: WorkLaptop);

        // Act
        PickupResult result = await (await PickupAsync(ct)).RunAsync(ct);

        // Assert
        result.Should().Be(new PickupResult(0, 0, 0, 0, null));
        InboxFiles().Should().BeEmpty();
        _processes.Calls.Should().BeEmpty();
        _log.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_TenantFieldIsOtherOrg_LeftWarnedOnceTenantMismatch_NeverRead()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        (string path, _) = await DropContextAsync(HomeLaptop, Fact + "\n", ct, mutate: b => Replace(b, "tenant: acme", "tenant: other-org"));

        // Act
        PickupResult result = await (await PickupAsync(ct)).RunAsync(ct);

        // Assert
        result.Should().Be(new PickupResult(0, 0, 0, 1, null));
        _log.Messages.Should().Equal($"pickup: left {path}: tenant_mismatch (tenant)");
        InboxFiles().Should().BeEmpty();
        _processes.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("malformed", "malformed (-)")]
    [InlineData("no-sig", "missing_signature (sig)")]
    [InlineData("unknown-key", "unknown_key (key_id)")]
    [InlineData("flipped-sig", "invalid_signature (sig)")]
    [InlineData("writer", "writer_mismatch (from)")]
    [InlineData("type-folder", "type_folder_mismatch (type)")]
    [InlineData("id-differs", "id_differs (id)")]
    public async Task RunAsync_RefusedFiles_LeftWithReasonNeverBody(string kind, string expected)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        const string Token = "token-in-the-body-7f3a";
        ISecretStore store = await StoreAsync(ct);
        string folder = BusPaths.Context(Acme, HomeLaptop);
        var id = EnvelopeId.New(T);
        byte[] bytes = kind switch
        {
            "malformed" => Encoding.UTF8.GetBytes("no front matter " + Token + "\n"),
            "no-sig" => StripSig(await ContextBytesAsync(HomeLaptop, Token, store, ct, id: id)),
            "unknown-key" => await TestEnvelopes.SignedContextBytesAsync(HomeLaptop, Central, await TestKeys.StoreWithAsync(TestKeys.Acme2, ct), TestKeys.Acme2, Token + "\n", T, ct, id: id),
            "flipped-sig" => FlipSig(await ContextBytesAsync(HomeLaptop, Token, store, ct, id: id)),
            "writer" => await ContextBytesAsync(WorkLaptop, Token, store, ct, id: id),
            "type-folder" => await TestEnvelopes.SignedReportBytesAsync(HomeLaptop, Central, EnvelopeId.New(T), store, TestKeys.Acme1, Token + "\n", ct, id: id, created: T),
            _ => await ContextBytesAsync(HomeLaptop, Token, store, ct),
        };
        string path = BusPaths.EnvelopeFile(folder, id);
        _checkout.Drop(path, bytes);

        // Act
        PickupResult result = await (await PickupAsync(ct)).RunAsync(ct);

        // Assert
        result.Left.Should().Be(1);
        _log.Messages.Should().Equal($"pickup: left {path}: {expected}");
        _log.Messages.Should().NotContain(m => m.Contains(Token, StringComparison.Ordinal));
        InboxFiles().Should().BeEmpty();
        _processes.CallsOf("mv").Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_LeftFile_SecondPass_NoSecondWarning()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropContextAsync(HomeLaptop, Fact + "\n", ct, mutate: FlipSig);
        BusPickup pickup = await PickupAsync(ct);

        // Act
        await pickup.RunAsync(ct);
        PickupResult second = await pickup.RunAsync(ct);

        // Assert
        second.Left.Should().Be(1);
        _log.AtLevel(LogLevel.Warning).Should().ContainSingle();
    }

    [Fact]
    public async Task RunAsync_SecretShapedSecondOfThreeLines_InboxHoldsLinesOneAndThree_WarningNamesPatternAndId_Archived()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        (_, EnvelopeId id) = await DropContextAsync(HomeLaptop, "- one\n- key " + AwsKey + "\n- three\n", ct);

        // Act
        PickupResult result = await (await PickupAsync(ct)).RunAsync(ct);

        // Assert
        result.Should().Be(new PickupResult(1, 0, 1, 0, new Pushed(1)));
        string inbox = InboxBody($"bus-context-home-laptop-{id}-2026-10-29.md");
        inbox.Should().Be("- one\n- three\n");
        _log.AtLevel(LogLevel.Warning).Select(e => e.Message).Should().Equal($"pickup: {id}: dropped 1 line (pattern aws-access-key)");
        _log.Messages.Concat([File.ReadAllText(_memory.Paths.InboxFile($"bus-context-home-laptop-{id}-2026-10-29.md"))])
            .Should().NotContain(m => m.Contains(AwsKey, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_EveryLineSecretShaped_NoInboxFile_StillArchived()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropContextAsync(HomeLaptop, "- key " + AwsKey + "\n\n", ct);

        // Act
        PickupResult result = await (await PickupAsync(ct)).RunAsync(ct);

        // Assert
        result.Should().Be(new PickupResult(0, 1, 1, 0, new Pushed(1)));
        InboxFiles().Should().BeEmpty();
        _processes.CallsOf("mv").Should().ContainSingle();
    }

    [Fact]
    public async Task RunAsync_InboxAlreadyPresent_NotRewritten_NoSecondFile_Archived()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        (_, EnvelopeId id) = await DropContextAsync(HomeLaptop, Fact + "\n", ct);
        string name = $"bus-context-home-laptop-{id}-2026-10-29.md";
        _memory.Write("inbox/" + name, "earlier copy\n");

        // Act
        PickupResult result = await (await PickupAsync(ct)).RunAsync(ct);

        // Assert
        result.Should().Be(new PickupResult(0, 1, 0, 0, new Pushed(1)));
        File.ReadAllText(_memory.Paths.InboxFile(name)).Should().Be("earlier copy\n");
        InboxFiles().Should().ContainSingle();
        _processes.CallsOf("mv").Should().ContainSingle();
    }

    [Fact]
    public async Task RunAsync_ArchiveMvFails_InboxKept_BusFilesStay_ErrorOncePerStreak_NextPassArchivesWithoutRewritingInbox()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        (_, EnvelopeId id) = await DropContextAsync(HomeLaptop, Fact + "\n", ct);
        _processes.On("mv", RecordingProcessRunner.Fail(128, "fatal: bad source"), RecordingProcessRunner.Fail(128, "fatal: bad source"), RecordingProcessRunner.Ok());
        BusPickup pickup = await PickupAsync(ct);
        string inbox = _memory.Paths.InboxFile($"bus-context-home-laptop-{id}-2026-10-29.md");

        // Act
        PickupResult first = await pickup.RunAsync(ct);
        var stamp = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(inbox, stamp);
        byte[] bytes = File.ReadAllBytes(inbox);
        PickupResult second = await pickup.RunAsync(ct);
        PickupResult third = await pickup.RunAsync(ct);

        // Assert
        first.Archive.Should().Be(new WriteFailed("mv", "fatal: bad source"));
        second.Archive.Should().BeOfType<WriteFailed>();
        third.Should().Be(new PickupResult(0, 1, 0, 0, new Pushed(1)));
        _log.AtLevel(LogLevel.Error).Select(e => e.Message).Should().Equal("pickup: archive failed at mv: fatal: bad source");
        File.ReadAllBytes(inbox).Should().Equal(bytes);
        File.GetLastWriteTimeUtc(inbox).Should().Be(stamp);
    }

    [Fact]
    public async Task RunAsync_ArchiveLockBusyAtListing_ErrorOnceNothingWritten()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropContextAsync(HomeLaptop, Fact + "\n", ct);
        Directory.CreateDirectory(Path.Combine(_checkout.Root, ".git"));
        using IDisposable? other = await BusCheckoutLock.AcquireAsync(_checkout.Root, BusCheckoutLock.WriteLockName, TimeSpan.Zero, _clock, ct);
        BusPickup pickup = await PickupAsync(ct);

        // Act
        Task<PickupResult> task = pickup.RunAsync(ct);
        for (int i = 0; i < 200 && !task.IsCompleted; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(5, ct);
        }

        PickupResult result = await task;

        // Assert
        result.Archive.Should().BeOfType<WriteFailed>().Which.Stage.Should().Be("lock");
        _log.AtLevel(LogLevel.Error).Select(e => e.Message).Should().Equal("pickup: archive failed at lock: " + BusBusyException.DefaultMessage);
        InboxFiles().Should().BeEmpty();
        _processes.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_PushRefusedFourTimes_ArchiveDeferred_InformationSaysDeferred()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        _provider.PushAsync(Arg.Any<CancellationToken>()).Returns(new Rejected("fetch first"));
        await DropContextAsync(HomeLaptop, Fact + "\n", ct);
        BusPickup pickup = await PickupAsync(ct);

        // Act
        Task<PickupResult> task = pickup.RunAsync(ct);
        for (int i = 0; i < 400 && !task.IsCompleted; i++)
        {
            _clock.Advance(TimeSpan.FromMilliseconds(500));
            await Task.Delay(2, ct);
        }

        PickupResult result = await task;

        // Assert
        result.Archive.Should().Be(new Deferred(4, "fetch first"));
        _log.AtLevel(LogLevel.Information).Should().ContainSingle().Which.Message.Should().EndWith("; archive deferred");
    }

    [Fact]
    public async Task RunAsync_TwoContextsAndOneReportFromTwoMachines_ThreeInboxFilesOneCommitOneInformationLine()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropContextAsync(HomeLaptop, "- one\n", ct);
        await DropReportAsync(HomeLaptop, EnvelopeId.New(T), "Done\n", ct);
        await DropContextAsync(WorkLaptop, "- two\n", ct);

        // Act
        PickupResult result = await (await PickupAsync(ct)).RunAsync(ct);

        // Assert
        result.Should().Be(new PickupResult(3, 0, 0, 0, new Pushed(1)));
        InboxFiles().Should().HaveCount(3);
        _processes.CallsOf("commit").Should().ContainSingle().Which.StandardInput.Should().StartWith("acme archive 3 central→archive\n\n");
        _log.AtLevel(LogLevel.Information).Should().ContainSingle().Which.Message.Should().StartWith("picked up 3 file(s) from 2 machine(s): 3 to inbox, ");
    }

    [Fact]
    public async Task RunAsync_InboxWriteFails_ThatFileNotArchived_OthersAre_ErrorOnce()
    {
        // Arrange: the context's inbox file name is squatted by a directory, so its write fails; the rejected report needs no inbox
        var ct = TestContext.Current.CancellationToken;
        (_, EnvelopeId id) = await DropContextAsync(HomeLaptop, Fact + "\n", ct);
        (string report, _) = await DropReportAsync(HomeLaptop, EnvelopeId.New(T), "rejected: locked\n", ct, ReportStatus.Rejected, "locked");
        Directory.CreateDirectory(_memory.Paths.InboxFile($"bus-context-home-laptop-{id}-2026-10-29.md"));
        Directory.CreateDirectory(Path.Combine(_memory.Paths.InboxFile($"bus-context-home-laptop-{id}-2026-10-29.md"), "x"));
        BusPickup pickup = await PickupAsync(ct);

        // Act
        PickupResult first = await pickup.RunAsync(ct);
        _processes.Calls.Clear();
        File.Delete(BusPaths.Resolve(_checkout.Root, report));
        PickupResult second = await pickup.RunAsync(ct);

        // Assert
        first.Should().Be(new PickupResult(0, 1, 0, 1, new Pushed(1)));
        second.Left.Should().Be(1);
        _log.AtLevel(LogLevel.Error).Select(e => e.Message).Should().ContainSingle().Which.Should().StartWith($"pickup: inbox write failed for {id}: ");
    }

    [Fact]
    public async Task RunAsync_UnexpectedException_ReturnsWithOneErrorNamingTheType_NeverThrows()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropContextAsync(HomeLaptop, Fact + "\n", ct);
        _store = new ThrowingSecretStore { Failure = new InvalidOperationException("store broke") };
        BusPickup pickup = await PickupAsync(ct);

        // Act
        PickupResult result = await pickup.RunAsync(ct);

        // Assert
        result.Should().Be(new PickupResult(0, 0, 0, 0, null));
        _log.AtLevel(LogLevel.Error).Select(e => e.Message).Should().Equal("pickup: pass failed: InvalidOperationException");
    }

    [Fact]
    public async Task RunAsync_Cancelled_ThrowsOperationCanceled()
    {
        // Arrange
        await DropContextAsync(HomeLaptop, Fact + "\n", TestContext.Current.CancellationToken);
        BusPickup pickup = await PickupAsync(TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Act
        Func<Task> act = () => pickup.RunAsync(cts.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task RunAsync_NothingToDo_NoLogLineNoCommit()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;

        // Act
        PickupResult result = await (await PickupAsync(ct)).RunAsync(ct);

        // Assert
        result.Should().Be(new PickupResult(0, 0, 0, 0, null));
        _log.Entries.Should().BeEmpty();
        _processes.Calls.Should().BeEmpty();
        await _provider.DidNotReceive().PullAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_EveryLineCarriesTenantMachinePhasePickupScope_NeverABodyLine()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropContextAsync(HomeLaptop, Fact + "\n- key " + AwsKey + "\n", ct);
        await DropContextAsync(HomeLaptop, Fact + "\n", ct, mutate: FlipSig);

        // Act
        await (await PickupAsync(ct)).RunAsync(ct);

        // Assert
        _log.Entries.Should().HaveCount(3);
        foreach (LogEntry entry in _log.Entries)
        {
            entry.Scopes.Should().ContainSingle().Which.Should().BeAssignableTo<IEnumerable<KeyValuePair<string, object>>>()
                .Which.Should().BeEquivalentTo(new Dictionary<string, object> { ["tenant"] = "acme", ["machine"] = "central", ["phase"] = "pickup" });
            entry.Message.Should().NotContain("staging database").And.NotContain(AwsKey);
        }
    }

    [Fact]
    public async Task RunAsync_OnlyGitCallsAreTheBusCheckout_NoClaude()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropContextAsync(HomeLaptop, Fact + "\n", ct);
        await DropReportAsync(HomeLaptop, EnvelopeId.New(T), "Done\n", ct);
        var before = TreeFingerprint.Of(_memory.Root).Where(f => !f.Path.Contains("/inbox/", StringComparison.Ordinal)).ToList();

        // Act
        await (await PickupAsync(ct)).RunAsync(ct);

        // Assert
        _processes.Calls.Should().NotBeEmpty().And.OnlyContain(c => c.WorkingDirectory == _checkout.Root && !c.FileName.EndsWith("claude", StringComparison.OrdinalIgnoreCase));
        TreeFingerprint.Of(_memory.Root).Where(f => !f.Path.Contains("/inbox/", StringComparison.Ordinal)).Should().Equal(before);
    }

    private static byte[] Replace(byte[] bytes, string from, string to) =>
        Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace(from, to, StringComparison.Ordinal));

    private static byte[] StripSig(byte[] bytes) =>
        Encoding.UTF8.GetBytes(string.Join('\n', Encoding.UTF8.GetString(bytes).Split('\n').Where(l => !l.StartsWith("sig:", StringComparison.Ordinal))));

    private static byte[] FlipSig(byte[] bytes)
    {
        string text = Encoding.UTF8.GetString(bytes);
        int at = text.IndexOf("hmac-sha256:", text.IndexOf("\nsig: ", StringComparison.Ordinal), StringComparison.Ordinal) + "hmac-sha256:".Length;
        char c = text[at] == '0' ? '1' : '0';
        return Encoding.UTF8.GetBytes(text[..at] + c + text[(at + 1)..]);
    }

    private string[] InboxFiles() =>
        Directory.Exists(_memory.Paths.InboxDirectory) ? [.. Directory.EnumerateFiles(_memory.Paths.InboxDirectory)] : [];

    private string InboxBody(string name) => File.ReadAllText(_memory.Paths.InboxFile(name)).Split("---\n")[2];

    private async Task<ISecretStore> StoreAsync(CancellationToken ct) => _store ??= await TestKeys.StoreWithAsync(TestKeys.Acme1, ct);

    private async Task<byte[]> ContextBytesAsync(MachineName from, string body, ISecretStore store, CancellationToken ct, MachineName? to = null, EnvelopeId? id = null, Func<byte[], byte[]>? mutate = null)
    {
        id ??= EnvelopeId.New(_clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        return await TestEnvelopes.SignedContextBytesAsync(from, to ?? Central, store, TestKeys.Acme1, body, T, ct, id: id, mutate: mutate);
    }

    private async Task<(string Path, EnvelopeId Id)> DropContextAsync(MachineName from, string body, CancellationToken ct, MachineName? to = null, Func<byte[], byte[]>? mutate = null)
    {
        var id = EnvelopeId.New(_clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        byte[] bytes = await ContextBytesAsync(from, body, await StoreAsync(ct), ct, to, id, mutate);
        string path = BusPaths.EnvelopeFile(BusPaths.Context(Acme, from), id);
        _checkout.Drop(path, bytes);
        return (path, id);
    }

    private async Task<(string Path, EnvelopeId Id)> DropReportAsync(MachineName from, EnvelopeId job, string body, CancellationToken ct, ReportStatus status = ReportStatus.Done, string? reason = null)
    {
        var id = EnvelopeId.New(_clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        byte[] bytes = await TestEnvelopes.SignedReportBytesAsync(from, Central, job, await StoreAsync(ct), TestKeys.Acme1, body, ct, status: status, reason: reason, id: id, created: T);
        string path = BusPaths.EnvelopeFile(BusPaths.Reports(Acme, from), id);
        _checkout.Drop(path, bytes);
        return (path, id);
    }

    private async Task<BusPickup> PickupAsync(CancellationToken ct)
    {
        ISecretStore store = await StoreAsync(ct);
        var git = new GitClient(_processes, new GitClientOptions { Identity = TestGitIdentities.Central }, _clock);
        var signer = new EnvelopeSigner(store);
        var bus = new BusRepository(git, _provider, new BusIdentity(Acme, Central), _checkout.Root, signer, _clock, () => 0.5, new RecordingLogger<BusRepository>());
        SecretPatterns patterns = SecretPatterns.Load(Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt")).Patterns!;
        return new BusPickup(bus, signer, new PickupInbox(_memory.Paths, HubHarness.Brussels, _clock), patterns, _log);
    }
}
