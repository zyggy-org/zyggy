using System.Text;

using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Zyggy.Core.Bus;
using Zyggy.Core.Bus.Probe;
using Zyggy.Core.Envelope;
using Zyggy.Core.Git;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Bus.Probe;

/// <summary>Spec 04 (the sender half of AC-26) over substitutes, a recorded git and a fake clock.</summary>
public sealed class ProbeSenderTests : IAsyncLifetime
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName Central = MachineName.Parse("central");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly BusIdentity Me = new(Acme, Central);

    private static readonly BusRemote Remote = new("acme-org", "acme-bus", "main", new Uri("https://example.invalid/acme-org/acme-bus.git"), null);

    private static readonly DateTimeOffset T = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);

    private readonly TempCheckout _checkout = new(Acme, Central);

    private readonly RecordingProcessRunner _processes = new();

    private readonly IBusProvider _provider = Substitute.For<IBusProvider>();

    private readonly RecordingLogger<ProbeSender> _log = new();

    private readonly FakeTimeProvider _clock = new(T);

    private readonly string _ledgerPath = Path.Combine(Path.GetTempPath(), "zyggy-unit", Guid.NewGuid().ToString("N"), "sent.jsonl");

    private InMemorySecretStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = await TestKeys.StoreWithBothAsync(CancellationToken.None);
        _provider.Remote.Returns(Remote);
        _provider.PullAsync(Arg.Any<CancellationToken>()).Returns(new BusSyncResult(true, string.Empty));
        _provider.PushAsync(Arg.Any<CancellationToken>()).Returns(new PushSucceeded());
        _provider.ProbeHeadAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new Unchanged(4999));
    }

    public ValueTask DisposeAsync()
    {
        _checkout.Dispose();
        if (Directory.Exists(Path.GetDirectoryName(_ledgerPath)!))
        {
            Directory.Delete(Path.GetDirectoryName(_ledgerPath)!, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task SendOneAsync_ReportObserved_Done()
    {
        // Arrange: the branch moves on the second tick and home-laptop's report is waiting
        var ct = TestContext.Current.CancellationToken;
        _provider.ProbeHeadAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new Unchanged(4999), new Changed("\"1\"", 4999));
        EnvelopeId? sentId = null;
        _provider.When(p => p.PullAsync(Arg.Any<CancellationToken>())).Do(_ => DropReportIfSentAsync(sentId, ReportStatus.Done, null, ct).GetAwaiter().GetResult());
        _processes.Hook = spec =>
        {
            if (RecordingProcessRunner.SubCommand(spec) == "add")
            {
                sentId = EnvelopeId.Parse(Path.GetFileNameWithoutExtension(spec.Arguments[^1]));
            }

            return null;
        };

        // Act
        Task<ProbeRecord> task = Sender().SendOneAsync(HomeLaptop, TimeSpan.FromSeconds(60), ct);
        ProbeRecord record = await DriveAsync(task);

        // Assert
        record.Outcome.Should().Be(ProbeOutcome.Done);
        record.To.Should().Be(HomeLaptop);
        record.RoundTripMs.Should().BeGreaterThan(0);
        record.ObservedUtc.Should().NotBeNull();
        new ProbeLedger(_ledgerPath).ReadAll().Should().ContainSingle().Which.Should().Be(record);
    }

    [Fact]
    public async Task SendOneAsync_NoReportBeforeTimeout_Timeout()
    {
        // Act
        var ct = TestContext.Current.CancellationToken;
        Task<ProbeRecord> task = Sender().SendOneAsync(HomeLaptop, TimeSpan.FromSeconds(30), ct);
        ProbeRecord record = await DriveAsync(task);

        // Assert
        record.Outcome.Should().Be(ProbeOutcome.Timeout);
        record.RoundTripMs.Should().BeNull();
        _clock.GetUtcNow().Should().BeOnOrAfter(T.AddSeconds(30));
        await _provider.Received().ProbeHeadAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _provider.Received(1).PullAsync(Arg.Any<CancellationToken>()); // the submit's own pull only; the poller never pulled
    }

    [Fact]
    public async Task SendOneAsync_SubmitDeferred_PushFailed()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        _provider.PushAsync(Arg.Any<CancellationToken>()).Returns(new Rejected("fetch first"));

        // Act
        Task<ProbeRecord> task = Sender().SendOneAsync(HomeLaptop, TimeSpan.FromSeconds(30), ct);
        ProbeRecord record = await DriveAsync(task);

        // Assert
        record.Outcome.Should().Be(ProbeOutcome.PushFailed);
        record.Reason.Should().StartWith("deferred:");
        await _provider.DidNotReceive().ProbeHeadAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendOneAsync_RejectedReport_RejectedWithReason()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        _provider.ProbeHeadAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new Changed("\"1\"", 4999));
        EnvelopeId? sentId = null;
        _provider.When(p => p.PullAsync(Arg.Any<CancellationToken>())).Do(_ => DropReportIfSentAsync(sentId, ReportStatus.Rejected, "wrong_recipient", ct).GetAwaiter().GetResult());
        _processes.Hook = spec =>
        {
            if (RecordingProcessRunner.SubCommand(spec) == "add")
            {
                sentId = EnvelopeId.Parse(Path.GetFileNameWithoutExtension(spec.Arguments[^1]));
            }

            return null;
        };

        // Act
        ProbeRecord record = await DriveAsync(Sender().SendOneAsync(HomeLaptop, TimeSpan.FromSeconds(60), ct));

        // Assert
        record.Outcome.Should().Be(ProbeOutcome.Rejected);
        record.Reason.Should().Be("wrong_recipient");
    }

    [Fact]
    public async Task SendOneAsync_ReportSignedWithKeySenderDoesNotHold_UnverifiedReportNotTimeout()
    {
        // Arrange: the sender holds acme/1 only; home-laptop answers with a report signed by acme/2 (decision 0001 F3)
        var ct = TestContext.Current.CancellationToken;
        _store = await TestKeys.StoreWithAsync(TestKeys.Acme1, ct);
        _provider.ProbeHeadAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new Changed("\"1\"", 4999));
        EnvelopeId? sentId = null;
        _provider.When(p => p.PullAsync(Arg.Any<CancellationToken>())).Do(_ => DropReportIfSentAsync(sentId, ReportStatus.Rejected, "wrong_recipient", ct, TestKeys.Acme2).GetAwaiter().GetResult());
        _processes.Hook = spec =>
        {
            if (RecordingProcessRunner.SubCommand(spec) == "add")
            {
                sentId = EnvelopeId.Parse(Path.GetFileNameWithoutExtension(spec.Arguments[^1]));
            }

            return null;
        };

        // Act
        ProbeRecord record = await DriveAsync(Sender().SendOneAsync(HomeLaptop, TimeSpan.FromSeconds(60), ct));

        // Assert: recorded as soon as the unverifiable report is seen, not at the deadline
        record.Outcome.Should().Be(ProbeOutcome.UnverifiedReport);
        record.Reason.Should().Be("unknown_key");
        record.RoundTripMs.Should().BeNull();
        record.ObservedUtc.Should().NotBeNull();
        _clock.GetUtcNow().Should().BeBefore(T.AddSeconds(60));
        new ProbeLedger(_ledgerPath).ReadAll().Should().ContainSingle().Which.Should().Be(record);
    }

    [Fact]
    public async Task SendOneAsync_ReportWithTamperedSignature_UnverifiedReportNotTimeout()
    {
        // Arrange: the report is signed with the right key but one signature hex digit is flipped on the bus
        var ct = TestContext.Current.CancellationToken;
        _provider.ProbeHeadAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new Changed("\"1\"", 4999));
        EnvelopeId? sentId = null;
        _provider.When(p => p.PullAsync(Arg.Any<CancellationToken>())).Do(_ => DropReportIfSentAsync(sentId, ReportStatus.Done, null, ct, TestKeys.Acme1, FlipLastSignatureDigit).GetAwaiter().GetResult());
        _processes.Hook = spec =>
        {
            if (RecordingProcessRunner.SubCommand(spec) == "add")
            {
                sentId = EnvelopeId.Parse(Path.GetFileNameWithoutExtension(spec.Arguments[^1]));
            }

            return null;
        };

        // Act
        ProbeRecord record = await DriveAsync(Sender().SendOneAsync(HomeLaptop, TimeSpan.FromSeconds(60), ct));

        // Assert
        record.Outcome.Should().Be(ProbeOutcome.UnverifiedReport);
        record.Reason.Should().Be("invalid_signature");
        record.RoundTripMs.Should().BeNull();
        _clock.GetUtcNow().Should().BeBefore(T.AddSeconds(60));
    }

    [Fact]
    public async Task SendOneAsync_JobFieldsFixed()
    {
        // Act
        var ct = TestContext.Current.CancellationToken;
        await DriveAsync(Sender().SendOneAsync(HomeLaptop, TimeSpan.FromSeconds(1), ct));

        // Assert: the pushed job has the fixed shape and verifies with acme/1
        string path = _checkout.FilesUnder(BusPaths.Jobs(Acme, HomeLaptop)).Should().ContainSingle().Subject;
        EnvelopeResult parsed = await new EnvelopeSigner(_store).ParseAndVerifyAsync(_checkout.Read(path), Acme, ct);
        parsed.IsAccepted.Should().BeTrue(parsed.Rejection?.ToString());
        Model job = parsed.Envelope!;
        job.Job!.Project.Should().Be("probe");
        job.Job.TimeoutMinutes.Should().Be(2);
        job.Header.From.Should().Be(Central);
        job.Header.To.Should().Be(HomeLaptop);
        job.BodyText.Should().Be(ProbeSender.Body);
        _processes.CallsOf("commit").Single().StandardInput.Should().Be($"acme job {job.Header.Id} central→home-laptop");
        SecretSweep.AssertAbsent(_processes.Calls.SelectMany(SecretSweep.TextsOf));
        SecretSweep.AssertAbsent(_log.Messages);
    }

    private ProbeSender Sender()
    {
        var git = new GitClient(_processes, new GitClientOptions(), _clock);
        var signer = new EnvelopeSigner(_store);
        var repository = new BusRepository(git, _provider, Me, _checkout.Root, signer, _clock, () => 0, new RecordingLogger<BusRepository>());
        return new ProbeSender(_provider, repository, signer, Me, TestKeys.Acme1, PollOptions.Default, _clock, new Random(3), new ProbeLedger(_ledgerPath), _log);
    }

    /// <summary>Advances the fake clock one second at a time until the sender finishes.</summary>
    private async Task<ProbeRecord> DriveAsync(Task<ProbeRecord> task)
    {
        for (int i = 0; i < 5000 && !task.IsCompleted; i++)
        {
            await Task.Delay(1, TestContext.Current.CancellationToken);
            _clock.Advance(TimeSpan.FromSeconds(1));
        }

        return await task;
    }

    /// <summary>The first pull is the submission's own (§5 step 1); the report can only exist once the job id is known.</summary>
    private async Task DropReportIfSentAsync(EnvelopeId? inReplyTo, ReportStatus status, string? reason, CancellationToken ct, KeyId? signWith = null, Func<byte[], byte[]>? tamper = null)
    {
        if (inReplyTo is null)
        {
            return;
        }

        var header = new EnvelopeHeader(Acme, EnvelopeId.New(_clock.GetUtcNow()), HomeLaptop, Central, _clock.GetUtcNow(), EnvelopePriority.Normal, inReplyTo);
        Model report = Model.CreateReport(header, new ReportFields(status, reason), Encoding.UTF8.GetBytes("ack\n"));
        EnvelopeResult signed = await new EnvelopeSigner(await TestKeys.StoreWithBothAsync(ct)).SignAsync(report, signWith ?? TestKeys.Acme1, ct);
        byte[] bytes = EnvelopeWriter.Serialize(signed.Envelope!);
        _checkout.Drop(BusPaths.EnvelopeFile(BusPaths.Reports(Acme, HomeLaptop), header.Id), tamper is null ? bytes : tamper(bytes));
    }

    /// <summary>Flips the last hexadecimal digit of the <c>sig</c> value so the HMAC no longer matches.</summary>
    private static byte[] FlipLastSignatureDigit(byte[] bytes)
    {
        string text = Encoding.UTF8.GetString(bytes);
        int start = text.IndexOf("sig: hmac-sha256:", StringComparison.Ordinal) + "sig: hmac-sha256:".Length;
        int last = start + 63;
        char flipped = text[last] == '0' ? '1' : '0';
        return Encoding.UTF8.GetBytes(string.Concat(text.AsSpan(0, last), flipped.ToString(), text.AsSpan(last + 1)));
    }
}
