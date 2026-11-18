using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;
using Zyggy.Core.Envelope;
using Zyggy.Core.Git;
using Zyggy.Core.Registry;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Bus;

/// <summary>
/// Spec 18 slice 7 through the seams: the node's mandate check before a claim (AC-16), the <c>mandate_invalid</c> wire name, the
/// <c>supervision</c> capability a node publishes, and every report writer copying <c>mandate</c> and <c>step</c> from its job.
/// </summary>
public sealed class MandateIntakeTests : IAsyncLifetime, IDisposable
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName Central = MachineName.Parse("central");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly DateTimeOffset T = new(2026, 11, 16, 10, 0, 0, TimeSpan.Zero);

    private static readonly EnvelopeId MandateId = EnvelopeId.New(T.AddHours(-1));

    private readonly TempCheckout _checkout = new(Acme, HomeLaptop);

    private readonly FakeTimeProvider _clock = new(T);

    private readonly SweepClaimsHarness _sweep = new();

    private InMemorySecretStore _store = null!;

    public async ValueTask InitializeAsync() => _store = await TestKeys.StoreWithBothAsync(CancellationToken.None);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose()
    {
        _checkout.Dispose();
        _sweep.Dispose();
    }

    [Fact]
    public void ToWire_MandateInvalid_SnakeCase() =>
        EnvelopeWire.ToWire(EnvelopeRejectionReason.MandateInvalid).Should().Be("mandate_invalid");

    [Theory]
    [InlineData(true, new[] { "supervision" })]
    [InlineData(false, new string[0])]
    public void FromScan_SupervisionSetting_CapabilityPublishedOnlyWhenEnabled(bool enabled, string[] expected)
    {
        // Arrange
        var configuration = new NodeConfiguration(Acme, HomeLaptop, TestKeys.Acme1,
            new BusSettings("acme-org", "acme-bus", "main", "/bus", "https://example.invalid/acme-org/acme-bus.git"),
            new PollSettings(), new SecretsSettings(SecretStoreKind.File, "/secrets"))
        {
            Supervision = new SupervisionSettings(Enabled: enabled),
        };

        // Act
        RegistryDocument document = RegistryDocument.FromScan(configuration, new ScanResult([], [], TimeSpan.Zero, DiscoveryDetail.Full), T, null);

        // Assert
        document.Capabilities.Should().Equal(expected);
    }

    [Fact]
    public async Task CheckAsync_JobWithoutMandate_PassesUntouched()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        AcceptedJob job = await JobAsync(mandate: null, ct);

        // Act
        JobIntake? intake = await Intake(enabled: true).CheckAsync(job, ct);

        // Assert
        intake.Should().BeSameAs(job);
    }

    [Fact]
    public async Task CheckAsync_SupervisionOff_RejectedMandateInvalidWithMandateAndStep()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        AcceptedJob job = await JobAsync(MandateId, ct);

        // Act
        JobIntake? intake = await Intake(enabled: false).CheckAsync(job, ct);

        // Assert
        intake.Should().BeOfType<RejectedJob>().Which.Should().BeEquivalentTo(new
        {
            Reason = EnvelopeRejectionReason.MandateInvalid,
            Field = "mandate",
            Detail = "supervision_off",
            From = Central,
            Mandate = MandateId,
            Step = "slice 1",
        });
    }

    [Fact]
    public async Task CheckAsync_MandateNotOnTheBus_RejectedNamingAbsent()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        AcceptedJob job = await JobAsync(MandateId, ct);

        // Act
        JobIntake? intake = await Intake(enabled: true).CheckAsync(job, ct);

        // Assert
        intake.Should().BeOfType<RejectedJob>().Which.Detail.Should().Be("absent");
    }

    [Fact]
    public async Task RunAsync_ExhaustedClaimUnderAMandate_ReportCarriesMandateAndStep()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await _sweep.DropClaimAsync(SweepClaimsHarness.HomeLaptop, ct, attempt: 3, mandate: MandateId, step: "slice 2");
        BusSweep sweep = await _sweep.SweepAsync(ct);

        // Act
        await sweep.RunAsync(ct);

        // Assert
        Model report = await _sweep.ReadVerifiedAsync(_sweep.Checkout.FilesUnder(BusPaths.Reports(Acme, Central)).Should().ContainSingle().Subject, ct);
        (report.Report!.Mandate, report.Report.Step).Should().Be((MandateId, "slice 2"));
    }

    [Fact]
    public async Task RunAsync_RefusedClaimUnderAMandate_RejectionReportCarriesMandateAndStep()
    {
        // Arrange: a claim in home-laptop's folder addressed to central is refused (wrong_recipient) by the sweep
        var ct = TestContext.Current.CancellationToken;
        await _sweep.DropClaimAsync(SweepClaimsHarness.HomeLaptop, ct, to: Central, mandate: MandateId, step: "slice 3");
        BusSweep sweep = await _sweep.SweepAsync(ct);

        // Act
        await sweep.RunAsync(ct);

        // Assert
        Model report = await _sweep.ReadVerifiedAsync(_sweep.Checkout.FilesUnder(BusPaths.Reports(Acme, Central)).Should().ContainSingle().Subject, ct);
        (report.Report!.Status, report.Report.Mandate, report.Report.Step).Should().Be((ReportStatus.Rejected, MandateId, "slice 3"));
    }

    private async Task<AcceptedJob> JobAsync(EnvelopeId? mandate, CancellationToken ct)
    {
        var fields = new JobFields("shop", Worktree: true, TimeoutMinutes: 2, AllowedTools: ["Read"], Mandate: mandate, Step: mandate is null ? null : "slice 1");
        Model job = await TestEnvelopes.SignedJobAsync(Central, HomeLaptop, _store, TestKeys.Acme1, "Build.\n", ct, fields: fields);
        return new AcceptedJob(new BusFile(BusPaths.EnvelopeFile(BusPaths.Jobs(Acme, HomeLaptop), job.Header.Id), job.Header.Id), job);
    }

    private MandateIntake Intake(bool enabled)
    {
        var git = new GitClient(new RecordingProcessRunner(), new GitClientOptions(), _clock);
        var signer = new EnvelopeSigner(_store);
        var bus = new BusRepository(git, Substitute.For<IBusProvider>(), new BusIdentity(Acme, HomeLaptop), _checkout.Root, signer, _clock, () => 0, new RecordingLogger<BusRepository>());
        var inventory = new BusInventory(bus, _checkout.Root, signer, _clock, new RecordingLogger<BusInventory>());
        return new MandateIntake(inventory, HomeLaptop, enabled, _clock, new RecordingLogger<MandateIntake>());
    }
}
