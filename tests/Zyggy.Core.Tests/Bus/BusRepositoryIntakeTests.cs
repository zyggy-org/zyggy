using System.Text;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Git;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Bus;

/// <summary>Spec 04 AC-17..AC-20, AC-23 (fake phase): intake order, report, rejection, submission and report lookup over a recorded git.</summary>
public sealed class BusRepositoryIntakeTests : IAsyncLifetime
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly TenantId Globex = TenantId.Parse("globex");

    private static readonly MachineName Central = MachineName.Parse("central");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly BusIdentity Me = new(Acme, HomeLaptop);

    private static readonly BusRemote Remote = new("acme-org", "acme-bus", "main", new Uri("https://example.invalid/acme-org/acme-bus.git"), null);

    private static readonly DateTimeOffset T = new(2026, 9, 30, 10, 0, 0, TimeSpan.Zero);

    private readonly TempCheckout _checkout = new(Acme, HomeLaptop);

    private readonly RecordingProcessRunner _processes = new();

    private readonly IBusProvider _provider = Substitute.For<IBusProvider>();

    private readonly RecordingLogger<BusRepository> _log = new();

    private readonly FakeTimeProvider _clock = new(T);

    private InMemorySecretStore _inner = null!;

    private ISecretStore _store = null!;

    public static TheoryData<string, EnvelopeRejectionReason, string?, bool> Rejections => new()
    {
        { "flipped", EnvelopeRejectionReason.InvalidSignature, "sig", true },
        { "globex", EnvelopeRejectionReason.TenantMismatch, "tenant", true },
        { "to-central", EnvelopeRejectionReason.WrongRecipient, "to", true },
        { "schema2", EnvelopeRejectionReason.SchemaUnsupported, "schema", true },
        { "no-front-matter", EnvelopeRejectionReason.Malformed, null, false },
        { "bad-from", EnvelopeRejectionReason.InvalidField, "from", false },
    };

    public async ValueTask InitializeAsync()
    {
        _inner = await TestKeys.StoreWithBothAsync(CancellationToken.None);
        _store = Substitute.For<ISecretStore>();
        _store.GetAsync(Arg.Any<TenantId>(), Arg.Any<SecretName>(), Arg.Any<CancellationToken>())
            .Returns(call => _inner.GetAsync(call.ArgAt<TenantId>(0), call.ArgAt<SecretName>(1), call.ArgAt<CancellationToken>(2)));
        _provider.Remote.Returns(Remote);
        _provider.PullAsync(Arg.Any<CancellationToken>()).Returns(new BusSyncResult(true, string.Empty));
        _provider.PushAsync(Arg.Any<CancellationToken>()).Returns(new PushSucceeded());
    }

    public ValueTask DisposeAsync()
    {
        _checkout.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task ListNewJobsAsync_TwoJobsAndClaimedAndRejectedFiles_ListsOnlyTopLevelInUlidOrder()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var older = EnvelopeId.New(T.AddMinutes(-5));
        var newer = EnvelopeId.New(T);
        _checkout.Drop(BusPaths.EnvelopeFile(BusPaths.Jobs(Acme, HomeLaptop), newer), await JobBytesAsync(ct, id: newer));
        _checkout.Drop(BusPaths.EnvelopeFile(BusPaths.Jobs(Acme, HomeLaptop), older), await JobBytesAsync(ct, id: older));
        _checkout.Drop(BusPaths.EnvelopeFile(BusPaths.Claimed(Acme, HomeLaptop), EnvelopeId.New(T)), [1]);
        _checkout.Drop(BusPaths.EnvelopeFile(BusPaths.Rejected(Acme, HomeLaptop), EnvelopeId.New(T)), [1]);
        _checkout.Drop(BusPaths.Jobs(Acme, HomeLaptop) + "/notes.txt", [1]);

        // Act
        IReadOnlyList<BusFile> files = await Repository().ListNewJobsAsync(ct);

        // Assert
        files.Select(f => f.Id).Should().Equal(older, newer);
        files.Select(f => f.RelativePath).Should().Equal(
            BusPaths.EnvelopeFile(BusPaths.Jobs(Acme, HomeLaptop), older),
            BusPaths.EnvelopeFile(BusPaths.Jobs(Acme, HomeLaptop), newer));
    }

    [Fact]
    public async Task ListNewJobsAsync_GlobexTenantFiles_NeverListedOrRead()
    {
        // Arrange: a foreign tenant's job beside mine, locked so any read would throw
        var ct = TestContext.Current.CancellationToken;
        string foreign = BusPaths.EnvelopeFile(BusPaths.Jobs(Globex, HomeLaptop), EnvelopeId.New(T));
        _checkout.Drop(foreign, [1, 2, 3]);
        using var lockHandle = new FileStream(BusPaths.Resolve(_checkout.Root, foreign), FileMode.Open, FileAccess.Read, FileShare.None);
        var mine = EnvelopeId.New(T);
        _checkout.Drop(BusPaths.EnvelopeFile(BusPaths.Jobs(Acme, HomeLaptop), mine), await JobBytesAsync(ct, id: mine));
        BusRepository repository = Repository();

        // Act
        IReadOnlyList<BusFile> files = await repository.ListNewJobsAsync(ct);
        var intakes = new List<JobIntake>();
        foreach (BusFile file in files)
        {
            intakes.Add(await repository.VerifyIntakeAsync(file, ct));
        }

        // Assert
        files.Select(f => f.Id).Should().Equal(mine);
        intakes.Should().ContainSingle().Which.Should().BeOfType<AcceptedJob>();
        _log.Messages.Should().NotContain(m => m.Contains("globex", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ListClaimedAsync_ListsOnlyOwnClaimedFolderInOrdinalOrder()
    {
        // Arrange: plan 07 Step 4 — the resume and the health count read this machine's claimed/ only
        var ct = TestContext.Current.CancellationToken;
        var older = EnvelopeId.New(T.AddMinutes(-5));
        var newer = EnvelopeId.New(T);
        _checkout.Drop(BusPaths.EnvelopeFile(BusPaths.Claimed(Acme, HomeLaptop), newer), [1]);
        _checkout.Drop(BusPaths.EnvelopeFile(BusPaths.Claimed(Acme, HomeLaptop), older), [1]);
        _checkout.Drop(BusPaths.EnvelopeFile(BusPaths.Jobs(Acme, HomeLaptop), EnvelopeId.New(T)), [1]);
        _checkout.Drop(BusPaths.EnvelopeFile(BusPaths.Claimed(Acme, Central), EnvelopeId.New(T)), [1]);
        _checkout.Drop(BusPaths.Claimed(Acme, HomeLaptop) + "/notes.txt", [1]);

        // Act
        IReadOnlyList<BusFile> files = await Repository().ListClaimedAsync(ct);

        // Assert
        files.Select(f => f.Id).Should().Equal(older, newer);
        files.Select(f => f.RelativePath).Should().Equal(
            BusPaths.EnvelopeFile(BusPaths.Claimed(Acme, HomeLaptop), older),
            BusPaths.EnvelopeFile(BusPaths.Claimed(Acme, HomeLaptop), newer));
    }

    [Fact]
    public async Task HeadShaAsync_RevParseHead()
    {
        // Act
        string? sha = await Repository().HeadShaAsync(TestContext.Current.CancellationToken);

        // Assert
        sha.Should().Be("0123456789abcdef0123456789abcdef01234567");
        _processes.CallsOf("rev-parse").Should().ContainSingle().Which.Arguments.Should().EndWith(["rev-parse", "HEAD"]);
    }

    [Fact]
    public async Task ListNewJobsAsync_EmptyFolder_Empty()
    {
        // Act
        IReadOnlyList<BusFile> files = await Repository().ListNewJobsAsync(TestContext.Current.CancellationToken);

        // Assert
        files.Should().BeEmpty();
    }

    [Fact]
    public async Task VerifyIntakeAsync_GoodJobToMe_Accepted()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        BusFile file = await DropJobAsync(ct);

        // Act
        JobIntake intake = await Repository().VerifyIntakeAsync(file, ct);

        // Assert
        var accepted = intake.Should().BeOfType<AcceptedJob>().Subject;
        accepted.Job.Header.To.Should().Be(HomeLaptop);
        accepted.File.Should().Be(file);
        _log.Entries.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Rejections))]
    public async Task VerifyIntakeAsync_Rejected_ReasonAndPeekedFrom(string variant, EnvelopeRejectionReason reason, string? field, bool fromReadable)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var id = EnvelopeId.New(T);
        byte[] bytes = variant switch
        {
            "flipped" => await JobBytesAsync(ct, id: id, mutate: b => { b[^2] ^= 0x01; return b; }),
            "globex" => await TestEnvelopes.SignedJobBytesAsync(Central, HomeLaptop, await StoreFor(Globex, ct), new KeyId(Globex, 1), "probe\n", ct, id: id),
            "to-central" => await TestEnvelopes.SignedJobBytesAsync(Central, Central, _inner, TestKeys.Acme1, "probe\n", ct, id: id),
            "schema2" => Rewrite(await JobBytesAsync(ct, id: id), "schema: 1", "schema: 2"),
            "no-front-matter" => Encoding.UTF8.GetBytes("just text\nfrom: central\n"),
            "bad-from" => Rewrite(await JobBytesAsync(ct, id: id), "from: central", "from: Not-A-Label"),
            _ => throw new ArgumentOutOfRangeException(nameof(variant)),
        };
        string path = BusPaths.EnvelopeFile(BusPaths.Jobs(Acme, HomeLaptop), id);
        _checkout.Drop(path, bytes);

        // Act
        JobIntake intake = await Repository().VerifyIntakeAsync(new BusFile(path, id), ct);

        // Assert
        var rejected = intake.Should().BeOfType<RejectedJob>().Subject;
        rejected.Reason.Should().Be(reason);
        rejected.Field.Should().Be(field);
        rejected.From.Should().Be(fromReadable ? Central : null);
        await _store.DidNotReceive().GetAsync(Globex, Arg.Any<SecretName>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VerifyIntakeAsync_IdDiffersFromFileName_AcceptedWithWarning()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var fileId = EnvelopeId.New(T.AddSeconds(1));
        string path = BusPaths.EnvelopeFile(BusPaths.Jobs(Acme, HomeLaptop), fileId);
        _checkout.Drop(path, await JobBytesAsync(ct, id: EnvelopeId.New(T)));

        // Act
        JobIntake intake = await Repository().VerifyIntakeAsync(new BusFile(path, fileId), ct);

        // Assert
        intake.Should().BeOfType<AcceptedJob>();
        _log.AtLevel(LogLevel.Warning).Should().ContainSingle().Which.Message.Should().Contain(fileId.Value);
    }

    [Fact]
    public async Task VerifyIntakeAsync_FileNameNotAUlid_MalformedWithoutFrom()
    {
        // Arrange: listed under a synthetic id; the file is never parsed
        var ct = TestContext.Current.CancellationToken;
        string path = BusPaths.Jobs(Acme, HomeLaptop) + "/weird.md";
        _checkout.Drop(path, await JobBytesAsync(ct));
        BusFile file = (await Repository().ListNewJobsAsync(ct)).Single();

        // Act
        JobIntake intake = await Repository().VerifyIntakeAsync(file, ct);

        // Assert
        var rejected = intake.Should().BeOfType<RejectedJob>().Subject;
        rejected.Reason.Should().Be(EnvelopeRejectionReason.Malformed);
        rejected.From.Should().BeNull();
        file.RelativePath.Should().Be(path);
    }

    [Fact]
    public async Task ReportAsync_Done_WritesSignedReportRemovesJobOneCommit()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        AcceptedJob job = await ClaimedJobAsync(ct);
        var fields = new ReportFields(ReportStatus.Done, Started: T.AddSeconds(-3), Finished: T, DurationSeconds: 3);

        // Act
        WriteOutcome outcome = await Repository().ReportAsync(job, fields, Encoding.UTF8.GetBytes("probe ok\n"), TestKeys.Acme1, ct);

        // Assert
        outcome.Should().Be(new Pushed(1));
        string reportPath = _checkout.FilesUnder(BusPaths.Reports(Acme, HomeLaptop)).Should().ContainSingle().Subject;
        EnvelopeResult parsed = await new EnvelopeSigner(_inner).ParseAndVerifyAsync(_checkout.Read(reportPath), Acme, ct);
        parsed.IsAccepted.Should().BeTrue(parsed.Rejection?.ToString());
        Model report = parsed.Envelope!;
        report.Header.InReplyTo.Should().Be(job.File.Id);
        report.Header.To.Should().Be(Central);
        report.Header.From.Should().Be(HomeLaptop);
        report.Report!.Status.Should().Be(ReportStatus.Done);
        report.Report.Started.Should().NotBeNull();
        report.Report.Finished.Should().NotBeNull();
        report.Report.DurationSeconds.Should().Be(3);
        report.BodyText.Should().Be("probe ok\n");
        string claimedPath = BusPaths.EnvelopeFile(BusPaths.Claimed(Acme, HomeLaptop), job.File.Id);
        _processes.Calls.Select(c => string.Join(' ', c.Arguments)).Should().Equal(
            "add -- " + reportPath,
            "rm -- " + claimedPath,
            "-c user.name=zyggy (home-laptop) -c user.email=home-laptop@zyggy.org commit --only -F - -- " + reportPath + " " + claimedPath);
        _processes.CallsOf("commit").Single().StandardInput.Should().Be($"acme report {report.Header.Id} home-laptop→central");
    }

    [Fact]
    public async Task ReportAsync_KeyMissing_WriteFailedSignNothingWritten()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        AcceptedJob job = await ClaimedJobAsync(ct);

        // Act
        WriteOutcome outcome = await Repository().ReportAsync(job, new ReportFields(ReportStatus.Done), ReadOnlyMemory<byte>.Empty, new KeyId(Acme, 9), ct);

        // Assert
        outcome.Should().BeOfType<WriteFailed>().Which.Stage.Should().Be("sign");
        _checkout.FilesUnder(BusPaths.Reports(Acme, HomeLaptop)).Should().BeEmpty();
        _processes.Calls.Should().BeEmpty();
        await _provider.DidNotReceive().PullAsync(Arg.Any<CancellationToken>());
        _log.AtLevel(LogLevel.Error).Should().ContainSingle();
    }

    [Fact]
    public async Task RejectAsync_WithFrom_MovesAndWritesRejectedReportInSameCommit()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var id = EnvelopeId.New(T);
        string path = BusPaths.EnvelopeFile(BusPaths.Jobs(Acme, HomeLaptop), id);
        _checkout.Drop(path, await TestEnvelopes.SignedJobBytesAsync(Central, Central, _inner, TestKeys.Acme1, "probe\n", ct, id: id));
        var rejected = (RejectedJob)await Repository().VerifyIntakeAsync(new BusFile(path, id), ct);

        // Act
        WriteOutcome outcome = await Repository().RejectAsync(rejected, TestKeys.Acme1, ct);

        // Assert
        outcome.Should().Be(new Pushed(1));
        string reportPath = _checkout.FilesUnder(BusPaths.Reports(Acme, HomeLaptop)).Should().ContainSingle().Subject;
        EnvelopeResult parsed = await new EnvelopeSigner(_inner).ParseAndVerifyAsync(_checkout.Read(reportPath), Acme, ct);
        parsed.IsAccepted.Should().BeTrue(parsed.Rejection?.ToString());
        Model report = parsed.Envelope!;
        report.Report!.Status.Should().Be(ReportStatus.Rejected);
        report.Report.Reason.Should().Be("wrong_recipient");
        report.Header.InReplyTo.Should().Be(id);
        report.Header.To.Should().Be(Central);
        report.Header.From.Should().Be(HomeLaptop);
        report.BodyText.Should().Be("rejected: wrong_recipient (to)\n");
        string rejectedPath = BusPaths.EnvelopeFile(BusPaths.Rejected(Acme, HomeLaptop), id);
        _processes.Calls.Select(c => string.Join(' ', c.Arguments)).Should().Equal(
            "mv -- " + path + " " + rejectedPath,
            "add -- " + reportPath,
            "-c user.name=zyggy (home-laptop) -c user.email=home-laptop@zyggy.org commit --only -F - -- " + path + " " + rejectedPath + " " + reportPath);
        _processes.CallsOf("commit").Single().StandardInput.Should().Be($"acme reject {id} home-laptop→central");
        _log.AtLevel(LogLevel.Warning).Should().ContainSingle();
    }

    [Theory]
    [MemberData(nameof(AllReasons))]
    public async Task RejectAsync_EveryReason_WireNameInReport(EnvelopeRejectionReason reason)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var id = EnvelopeId.New(T);
        string path = BusPaths.EnvelopeFile(BusPaths.Jobs(Acme, HomeLaptop), id);
        _checkout.Drop(path, [1]);
        var rejected = new RejectedJob(new BusFile(path, id), reason, null, "test", Central);

        // Act
        await Repository().RejectAsync(rejected, TestKeys.Acme1, ct);

        // Assert
        string reportPath = _checkout.FilesUnder(BusPaths.Reports(Acme, HomeLaptop)).Single();
        Model report = (await new EnvelopeSigner(_inner).ParseAndVerifyAsync(_checkout.Read(reportPath), Acme, ct)).Envelope!;
        EnvelopeWire.TryFromWire(report.Report!.Reason, out EnvelopeRejectionReason back).Should().BeTrue();
        back.Should().Be(reason);
        report.BodyText.Should().Be("rejected: " + EnvelopeWire.ToWire(reason) + "\n");
    }

    [Fact]
    public async Task RejectAsync_FromUnreadable_MoveOnlyNoReportOneWarningNamingFile()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string path = BusPaths.Jobs(Acme, HomeLaptop) + "/weird.md";
        _checkout.Drop(path, Encoding.UTF8.GetBytes("not an envelope\n"));
        BusRepository repository = Repository();
        BusFile file = (await repository.ListNewJobsAsync(ct)).Single();
        var rejected = (RejectedJob)await repository.VerifyIntakeAsync(file, ct);

        // Act
        WriteOutcome outcome = await repository.RejectAsync(rejected, TestKeys.Acme1, ct);

        // Assert
        outcome.Should().Be(new Pushed(1));
        _checkout.FilesUnder(BusPaths.Reports(Acme, HomeLaptop)).Should().BeEmpty();
        _processes.Calls.Select(RecordingProcessRunner.SubCommand).Should().Equal("mv", "commit");
        _processes.CallsOf("mv").Single().Arguments[2].Should().Be(path);
        _processes.CallsOf("commit").Single().StandardInput.Should().EndWith("home-laptop→unknown");
        var warning = _log.AtLevel(LogLevel.Warning).Should().ContainSingle().Subject;
        warning.Message.Should().Contain("weird.md").And.Contain("malformed");
    }

    [Fact]
    public async Task RejectAsync_NeverLogsBody()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var id = EnvelopeId.New(T);
        string path = BusPaths.EnvelopeFile(BusPaths.Jobs(Acme, HomeLaptop), id);
        _checkout.Drop(path, await TestEnvelopes.SignedJobBytesAsync(Central, Central, _inner, TestKeys.Acme1, "SECRET-BODY-TEXT\n", ct, id: id));
        BusRepository repository = Repository();
        var rejected = (RejectedJob)await repository.VerifyIntakeAsync(new BusFile(path, id), ct);

        // Act
        await repository.RejectAsync(rejected, TestKeys.Acme1, ct);

        // Assert
        _log.Messages.Should().NotContain(m => m.Contains("SECRET-BODY-TEXT", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SubmitAsync_ToHomeLaptop_AddsSignedFileUnderTargetJobsWithJobCommit()
    {
        // Arrange: a sender (central) with its own checkout
        var ct = TestContext.Current.CancellationToken;
        using var senderCheckout = new TempCheckout(Acme, Central);
        Model job = await TestEnvelopes.SignedJobAsync(Central, HomeLaptop, _inner, TestKeys.Acme1, "probe\n", ct);
        BusRepository sender = Repository(new BusIdentity(Acme, Central), senderCheckout.Root);

        // Act
        WriteOutcome outcome = await sender.SubmitAsync(job, ct);

        // Assert
        outcome.Should().Be(new Pushed(1));
        string path = BusPaths.EnvelopeFile(BusPaths.Jobs(Acme, HomeLaptop), job.Header.Id);
        senderCheckout.Read(path).Should().Equal(EnvelopeWriter.Serialize(job));
        _processes.Calls.Select(RecordingProcessRunner.SubCommand).Should().Equal("add", "commit");
        _processes.CallsOf("commit").Single().StandardInput.Should().Be($"acme job {job.Header.Id} central→home-laptop");
    }

    [Fact]
    public async Task SubmitAsync_ToOtherTenant_ThrowsWriteRule()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Model job = await TestEnvelopes.SignedJobAsync(Central, HomeLaptop, await StoreFor(Globex, ct), new KeyId(Globex, 1), "probe\n", ct);

        // Act
        Func<Task> act = () => Repository(new BusIdentity(Acme, Central)).SubmitAsync(job, ct);

        // Assert
        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("write rule:*");
        _processes.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task SubmitAsync_Unsigned_ThrowsInvalidOperation()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Model unsigned = Model.CreateJob(new EnvelopeHeader(Acme, EnvelopeId.New(T), Central, HomeLaptop, T), new JobFields("probe"), ReadOnlyMemory<byte>.Empty);

        // Act
        Func<Task> act = () => Repository(new BusIdentity(Acme, Central)).SubmitAsync(unsigned, ct);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        _processes.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task FindReportsAsync_ReportForMyJob_Returned()
    {
        // Arrange: central looks for home-laptop's answer to its job
        var ct = TestContext.Current.CancellationToken;
        using var senderCheckout = new TempCheckout(Acme, Central);
        var jobId = EnvelopeId.New(T);
        byte[] mine = await ReportBytesAsync(HomeLaptop, Central, jobId, ct);
        byte[] otherJob = await ReportBytesAsync(HomeLaptop, Central, EnvelopeId.New(T.AddSeconds(5)), ct);
        byte[] otherTo = await ReportBytesAsync(HomeLaptop, MachineName.Parse("work-laptop"), jobId, ct);
        byte[] unverifiable = await ReportBytesAsync(HomeLaptop, Central, jobId, ct);
        unverifiable[^1] ^= 0x01;
        string folder = BusPaths.Reports(Acme, HomeLaptop);
        senderCheckout.Drop(BusPaths.EnvelopeFile(folder, EnvelopeId.New(T.AddSeconds(1))), mine);
        senderCheckout.Drop(BusPaths.EnvelopeFile(folder, EnvelopeId.New(T.AddSeconds(2))), otherJob);
        senderCheckout.Drop(BusPaths.EnvelopeFile(folder, EnvelopeId.New(T.AddSeconds(3))), otherTo);
        senderCheckout.Drop(BusPaths.EnvelopeFile(folder, EnvelopeId.New(T.AddSeconds(4))), unverifiable);

        // Act
        IReadOnlyList<Model> reports = await Repository(new BusIdentity(Acme, Central), senderCheckout.Root).FindReportsAsync(HomeLaptop, jobId, ct);

        // Assert
        Model report = reports.Should().ContainSingle().Subject;
        report.Header.InReplyTo.Should().Be(jobId);
        report.Header.To.Should().Be(Central);
        EnvelopeWriter.Serialize(report).Should().Equal(mine);
    }

    [Fact]
    public async Task FindReportsAsync_NoReportsFolder_Empty()
    {
        // Act
        IReadOnlyList<Model> reports = await Repository().FindReportsAsync(MachineName.Parse("nobody"), EnvelopeId.New(T), TestContext.Current.CancellationToken);

        // Assert
        reports.Should().BeEmpty();
    }

    [Fact]
    public async Task SecretSweep_Clean()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        BusRepository repository = Repository();
        AcceptedJob job = await ClaimedJobAsync(ct);
        await repository.ReportAsync(job, new ReportFields(ReportStatus.Done), ReadOnlyMemory<byte>.Empty, TestKeys.Acme1, ct);
        var id = EnvelopeId.New(T);
        string path = BusPaths.EnvelopeFile(BusPaths.Jobs(Acme, HomeLaptop), id);
        _checkout.Drop(path, await TestEnvelopes.SignedJobBytesAsync(Central, Central, _inner, TestKeys.Acme1, "probe\n", ct, id: id));
        await repository.RejectAsync((RejectedJob)await repository.VerifyIntakeAsync(new BusFile(path, id), ct), TestKeys.Acme1, ct);

        // Act & Assert
        SecretSweep.AssertAbsent(_processes.Calls.SelectMany(SecretSweep.TextsOf));
        SecretSweep.AssertAbsent(_log.Messages);
    }

    public static TheoryData<EnvelopeRejectionReason> AllReasons()
    {
        var data = new TheoryData<EnvelopeRejectionReason>();
        foreach (EnvelopeRejectionReason reason in Enum.GetValues<EnvelopeRejectionReason>())
        {
            data.Add(reason);
        }

        return data;
    }

    private static byte[] Rewrite(byte[] bytes, string from, string to) =>
        Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace(from, to, StringComparison.Ordinal));

    private static async Task<InMemorySecretStore> StoreFor(TenantId tenant, CancellationToken ct)
    {
        var store = new InMemorySecretStore();
        await store.SetAsync(tenant, new KeyId(tenant, 1).SecretName, TestKeys.Secret1, ct);
        return store;
    }

    private BusRepository Repository(BusIdentity? me = null, string? root = null)
    {
        var git = new GitClient(_processes, new GitClientOptions { Identity = new GitIdentity("zyggy (home-laptop)", "home-laptop@zyggy.org") }, _clock);
        return new BusRepository(git, _provider, me ?? Me, root ?? _checkout.Root, new EnvelopeSigner(_store), _clock, () => 0, _log);
    }

    private Task<byte[]> JobBytesAsync(CancellationToken ct, EnvelopeId? id = null, Func<byte[], byte[]>? mutate = null) =>
        TestEnvelopes.SignedJobBytesAsync(Central, HomeLaptop, _inner, TestKeys.Acme1, "probe\n", ct, mutate, id);

    private async Task<BusFile> DropJobAsync(CancellationToken ct)
    {
        var id = EnvelopeId.New(T);
        string path = BusPaths.EnvelopeFile(BusPaths.Jobs(Acme, HomeLaptop), id);
        _checkout.Drop(path, await JobBytesAsync(ct, id: id));
        return new BusFile(path, id);
    }

    private async Task<AcceptedJob> ClaimedJobAsync(CancellationToken ct)
    {
        var id = EnvelopeId.New(T);
        Model job = await TestEnvelopes.SignedJobAsync(Central, HomeLaptop, _inner, TestKeys.Acme1, "probe\n", ct, id);
        string claimed = BusPaths.EnvelopeFile(BusPaths.Claimed(Acme, HomeLaptop), id);
        _checkout.Drop(claimed, EnvelopeWriter.Serialize(job));
        return new AcceptedJob(new BusFile(BusPaths.EnvelopeFile(BusPaths.Jobs(Acme, HomeLaptop), id), id), job);
    }

    private async Task<byte[]> ReportBytesAsync(MachineName from, MachineName to, EnvelopeId inReplyTo, CancellationToken ct)
    {
        var header = new EnvelopeHeader(Acme, EnvelopeId.New(T), from, to, T, EnvelopePriority.Normal, inReplyTo);
        Model report = Model.CreateReport(header, new ReportFields(ReportStatus.Done), Encoding.UTF8.GetBytes("ok\n"));
        EnvelopeResult signed = await new EnvelopeSigner(_inner).SignAsync(report, TestKeys.Acme1, ct);
        return EnvelopeWriter.Serialize(signed.Envelope!);
    }
}
