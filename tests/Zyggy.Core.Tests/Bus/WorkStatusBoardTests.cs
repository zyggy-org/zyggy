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

// Spec 18 slice 2: the pure rules behind `zyggy work` and the hooks (AC-5 boundaries), and the auditor's rules for the status folder (AC-9).
public sealed class WorkStatusBoardTests : IAsyncLifetime
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly MachineName WorkLaptop = MachineName.Parse("work-laptop");

    private static readonly DateTimeOffset T = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);

    private readonly TempCheckout _checkout = new(Acme, HomeLaptop);

    private readonly FakeTimeProvider _clock = new(T);

    private InMemorySecretStore _store = null!;

    public async ValueTask InitializeAsync() => _store = await TestKeys.StoreWithBothAsync(CancellationToken.None);

    public ValueTask DisposeAsync()
    {
        _checkout.Dispose();
        return ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData(0, 0, SessionActivity.Working)]
    [InlineData(599, 599, SessionActivity.Working)]
    [InlineData(600, 600, SessionActivity.Idle)]
    [InlineData(3599, 3599, SessionActivity.Idle)]
    [InlineData(3600, 3600, SessionActivity.Silent)]
    [InlineData(3600, 30, SessionActivity.Silent)]
    [InlineData(3599, 30, SessionActivity.Working)]
    public void Classify_ByFileAgeAndLastTurnAge_WorkingIdleSilent(int writtenSecondsAgo, int activitySecondsAgo, SessionActivity expected)
    {
        // Act
        SessionActivity activity = BusInventory.Classify(T, T.AddSeconds(-writtenSecondsAgo), T.AddSeconds(-activitySecondsAgo));

        // Assert
        activity.Should().Be(expected);
    }

    [Theory]
    [InlineData("0b7c3d1e-4f5a-4b6c-8d7e-9f0a1b2c3d4e", "0b7c3d1e")]
    [InlineData("0B7C3D1E-4f5a", "0b7c3d1e")]
    [InlineData("abcdefgh", "abcdefgh")]
    [InlineData("0b7c-d1e-4f5a", null)]
    [InlineData("short", null)]
    [InlineData("0b7c3d1e/../x", null)]
    [InlineData(null, null)]
    public void SessionOf_FirstEightLettersOrDigitsLowercased_OrNull(string? sessionId, string? expected)
    {
        // Act
        string? session = WorkHookInput.SessionOf(sessionId);

        // Assert
        session.Should().Be(expected);
    }

    [Theory]
    [InlineData("feature/12-booking-pool", new[] { "12-booking-pool.md" }, "_specs/12-booking-pool.md")]
    [InlineData("feature/12-booking-pool", new[] { "12-booking-pool.md", "12-other.md" }, null)]
    [InlineData("feature/12-booking-pool", new[] { "13-other.md" }, null)]
    [InlineData("feature/12-booking-pool", new string[0], null)]
    [InlineData("main", new[] { "12-booking-pool.md" }, null)]
    [InlineData("bugfix/12-x", new[] { "12-booking-pool.md" }, null)]
    [InlineData(null, new[] { "12-booking-pool.md" }, null)]
    public void SpecOf_ExactlyOneSpecWithTheBranchNumber_OrNull(string? branch, string[] specFiles, string? expected)
    {
        // Arrange
        string project = Path.Combine(_checkout.Root, "project");
        Directory.CreateDirectory(Path.Combine(project, "_specs"));
        foreach (string file in specFiles)
        {
            File.WriteAllText(Path.Combine(project, "_specs", file), "# spec\n");
        }

        // Act
        string? spec = WorkStatusHook.SpecOf(project, branch);

        // Assert
        spec.Should().Be(expected);
    }

    [Theory]
    [InlineData(@"C:\src\My Project\", "My Project")]
    [InlineData("/home/alice/src/calizr", "calizr")]
    [InlineData("calizr", "calizr")]
    public void LastSegment_EitherSeparator_TheFolderName(string folder, string expected)
    {
        // Act
        string segment = WorkHookInput.LastSegment(folder);

        // Assert
        segment.Should().Be(expected);
    }

    [Fact]
    public async Task Audit_StatusFromItsFolderMachineToCentral_Ok()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var id = EnvelopeId.New(T);
        _checkout.Drop(BusPaths.EnvelopeFile(BusPaths.Status(Acme, HomeLaptop), id), await StatusAsync(id, HomeLaptop, ct));

        // Act
        AuditReport report = await Auditor().RunAsync(null, ct);

        // Assert
        report.Entries.Should().ContainSingle().Which.Outcome.Should().Be(AuditOutcome.Ok);
    }

    [Fact]
    public async Task Audit_StatusFromAnotherMachine_WriterMismatchFrom()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var id = EnvelopeId.New(T);
        _checkout.Drop(BusPaths.EnvelopeFile(BusPaths.Status(Acme, HomeLaptop), id), await StatusAsync(id, WorkLaptop, ct));

        // Act
        AuditReport report = await Auditor().RunAsync(null, ct);

        // Assert
        AuditEntry entry = report.Entries.Should().ContainSingle().Subject;
        (entry.Outcome, entry.Field).Should().Be((AuditOutcome.WriterMismatch, "from"));
    }

    [Fact]
    public async Task Audit_JobInAStatusFolder_TypeFolderMismatch()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var id = EnvelopeId.New(T);
        byte[] job = await TestEnvelopes.SignedJobBytesAsync(HomeLaptop, MachineName.Central, _store, TestKeys.Acme1, "probe\n", ct, id: id);
        _checkout.Drop(BusPaths.EnvelopeFile(BusPaths.Status(Acme, HomeLaptop), id), job);

        // Act
        AuditReport report = await Auditor().RunAsync(null, ct);

        // Assert
        AuditEntry entry = report.Entries.Should().ContainSingle().Subject;
        (entry.Outcome, entry.Field, entry.Detail).Should().Be((AuditOutcome.TypeFolderMismatch, "type", "job in status/"));
    }

    [Fact]
    public async Task Audit_StatusAddressedToAnotherMachine_InvalidFieldTo()
    {
        // Arrange: the parser already refuses a status that does not go to Central, so the auditor needs no rule of its own
        var ct = TestContext.Current.CancellationToken;
        var id = EnvelopeId.New(T);
        byte[] toLaptop = EnvelopeText.From(await StatusAsync(id, HomeLaptop, ct)).WithValue("to", "work-laptop").ToBytes();
        _checkout.Drop(BusPaths.EnvelopeFile(BusPaths.Status(Acme, HomeLaptop), id), toLaptop);

        // Act
        AuditReport report = await Auditor().RunAsync(null, ct);

        // Assert
        AuditEntry entry = report.Entries.Should().ContainSingle().Subject;
        entry.Outcome.Should().NotBe(AuditOutcome.Ok);
        entry.Field.Should().Be("to");
    }

    private async Task<byte[]> StatusAsync(EnvelopeId id, MachineName from, CancellationToken ct)
    {
        Model status = Model.CreateStatus(new EnvelopeHeader(Acme, id, from, MachineName.Central, T), new StatusFields("calizr", "5b3fd3ac", T.AddHours(-1), T));
        EnvelopeResult signed = await new EnvelopeSigner(_store).SignAsync(status, TestKeys.Acme1, ct);
        return EnvelopeWriter.Serialize(signed.Envelope!);
    }

    private BusAuditor Auditor()
    {
        var git = new GitClient(new RecordingProcessRunner(), new GitClientOptions { Identity = new GitIdentity("zyggy (home-laptop)", "home-laptop@zyggy.org") }, _clock);
        var signer = new EnvelopeSigner(_store);
        var bus = new BusRepository(git, Substitute.For<IBusProvider>(), new BusIdentity(Acme, HomeLaptop), _checkout.Root, signer, _clock, () => 0, new RecordingLogger<BusRepository>());
        return new BusAuditor(bus, _checkout.Root, signer, _clock, new RecordingLogger<BusAuditor>());
    }
}
