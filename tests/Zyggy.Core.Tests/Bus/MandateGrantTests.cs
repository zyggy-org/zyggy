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

// AC-13 (unit side): Central grants a mandate as a signed file in mandates/, refuses a field out of its range and any machine but
// Central before anything is signed or written, and revokes by moving the live file.
public sealed class MandateGrantTests : IAsyncLifetime
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName Central = MachineName.Central;

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly DateTimeOffset T = new(2026, 11, 16, 10, 0, 0, TimeSpan.Zero);

    private readonly TempCheckout _checkout = new(Acme, Central);

    private readonly RecordingProcessRunner _processes = new();

    private readonly IBusProvider _provider = Substitute.For<IBusProvider>();

    private readonly FakeTimeProvider _clock = new(T);

    private InMemorySecretStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _provider.PullAsync(Arg.Any<CancellationToken>()).Returns(new BusSyncResult(true, string.Empty));
        _provider.PushAsync(Arg.Any<CancellationToken>()).Returns(new PushSucceeded());
        _store = await TestKeys.StoreWithBothAsync(CancellationToken.None);
    }

    public ValueTask DisposeAsync()
    {
        _checkout.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task GrantAsync_OnCentral_SignedMandateUnderMandates_FromCentralToTheMachine()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;

        // Act
        AskDecideResult result = await Granter(Central).GrantAsync(HomeLaptop, Fields(), ct);

        // Assert
        var published = result.Should().BeOfType<AskDecidePublished>().Subject;
        published.Path.Should().Be(BusPaths.EnvelopeFile(BusPaths.Mandates(Acme), published.Id));
        EnvelopeResult read = await new EnvelopeSigner(_store).ParseAndVerifyAsync(_checkout.Read(published.Path), Acme, ct);
        read.IsAccepted.Should().BeTrue(read.Rejection?.ToString());
        read.Envelope!.Header.From.Should().Be(Central);
        read.Envelope.Header.To.Should().Be(HomeLaptop);
        read.Envelope.Header.Created.Should().Be(T);
        read.Envelope.Mandate.Should().BeEquivalentTo(Fields());
    }

    [Fact]
    public async Task GrantAsync_OnALaptop_RefusedMachine_NothingWritten()
    {
        // Act
        AskDecideResult result = await Granter(HomeLaptop).GrantAsync(HomeLaptop, Fields(), TestContext.Current.CancellationToken);

        // Assert
        result.Should().Be(new AskDecideRefused("machine", "only central grants a mandate"));
        _processes.Calls.Should().BeEmpty();
    }

    public static TheoryData<string, string> OutOfRange() => new()
    {
        { "max_jobs", "max_jobs" },
        { "max_jobs_per_hour", "max_jobs_per_hour" },
        { "max_jobs_per_step", "max_jobs_per_step" },
        { "expires", "expires" },
        { "goal", "goal" },
        { "branches", "branches" },
        { "spec_commit", "spec_commit" },
    };

    [Theory]
    [MemberData(nameof(OutOfRange))]
    public async Task GrantAsync_FieldOutOfRange_RefusedNamingIt_NothingWritten(string fault, string field)
    {
        // Arrange
        MandateFields fields = fault switch
        {
            "max_jobs" => Fields() with { MaxJobs = 201 },
            "max_jobs_per_hour" => Fields() with { MaxJobsPerHour = 61 },
            "max_jobs_per_step" => Fields() with { MaxJobsPerStep = 21 },
            "expires" => Fields() with { Expires = T.AddDays(7).AddSeconds(1) },
            "goal" => Fields() with { Goal = new string('g', 201) },
            "branches" => Fields() with { Branches = ["a", "b", "c", "d", "e", "f"] },
            _ => Fields() with { Spec = "_specs/12-booking-pool.md", SpecCommit = "abc" },
        };

        // Act
        AskDecideResult result = await Granter(Central).GrantAsync(HomeLaptop, fields, TestContext.Current.CancellationToken);

        // Assert
        result.Should().BeOfType<AskDecideRefused>().Which.Field.Should().Be(field);
        _processes.Calls.Should().BeEmpty();
        _checkout.FilesUnder(BusPaths.Mandates(Acme)).Should().BeEmpty();
    }

    [Fact]
    public async Task GrantAsync_ExpiresExactlySevenDaysAhead_Granted()
    {
        // Act
        AskDecideResult result = await Granter(Central).GrantAsync(HomeLaptop, Fields() with { Expires = T.AddDays(7) }, TestContext.Current.CancellationToken);

        // Assert
        result.Should().BeOfType<AskDecidePublished>();
    }

    [Fact]
    public async Task RevokeAsync_LiveMandate_MovedToRevoked_OneCommitNamingRevoke()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Model mandate = await DropMandateAsync(ct);

        // Act
        AskDecideResult result = await Granter(Central).RevokeAsync(mandate.Header.Id, ct);

        // Assert
        result.Should().BeOfType<AskDecidePublished>().Which.Path.Should().Be(BusPaths.EnvelopeFile(BusPaths.RevokedMandates(Acme), mandate.Header.Id));
        _processes.CallsOf("commit").Should().ContainSingle().Which.StandardInput.Should().StartWith($"acme revoke {mandate.Header.Id.Value} central→home-laptop");
    }

    [Fact]
    public async Task RevokeAsync_NoLiveMandate_NotFound_NoGitCall()
    {
        // Act
        AskDecideResult result = await Granter(Central).RevokeAsync(EnvelopeId.New(T), TestContext.Current.CancellationToken);

        // Assert
        result.Should().BeOfType<AskDecideNotFound>();
        _processes.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task RevokeAsync_AlreadyRevoked_NotFound_NoGitCall()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Model mandate = await DropMandateAsync(ct, revoked: true);

        // Act
        AskDecideResult result = await Granter(Central).RevokeAsync(mandate.Header.Id, ct);

        // Assert
        result.Should().BeOfType<AskDecideNotFound>();
        _processes.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task RevokeAsync_OnALaptop_RefusedMachine_NoGitCall()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Model mandate = await DropMandateAsync(ct);

        // Act
        AskDecideResult result = await Granter(HomeLaptop).RevokeAsync(mandate.Header.Id, ct);

        // Assert
        result.Should().BeOfType<AskDecideRefused>().Which.Field.Should().Be("machine");
        _processes.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task MandatesAsync_LiveAndRevoked_OneLedgerEach_OldestFirst()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Model revoked = await DropMandateAsync(ct, revoked: true);
        _clock.Advance(TimeSpan.FromMinutes(1));
        Model live = await DropMandateAsync(ct);

        // Act
        IReadOnlyList<MandateLedger> ledgers = await Inventory(Central).MandatesAsync(ct);

        // Assert
        ledgers.Select(l => (l.Id, l.Revoked, l.Mandate is not null)).Should().Equal((revoked.Header.Id, true, true), (live.Header.Id, false, true));
    }

    private static MandateFields Fields() => new(
        "calizr",
        "Build the booking pool",
        ["Read", "Edit", "Bash(dotnet test *)"],
        3,
        T.AddDays(3),
        Branches: ["feature/12-booking-pool"]);

    private async Task<Model> DropMandateAsync(CancellationToken ct, bool revoked = false)
    {
        DateTimeOffset at = _clock.GetUtcNow();
        Model mandate = Model.CreateMandate(new EnvelopeHeader(Acme, EnvelopeId.New(at), Central, HomeLaptop, at), Fields() with { Expires = at.AddDays(3) });
        Model signed = (await new EnvelopeSigner(_store).SignAsync(mandate, TestKeys.Acme1, ct)).Envelope!;
        _checkout.Drop(BusPaths.EnvelopeFile(revoked ? BusPaths.RevokedMandates(Acme) : BusPaths.Mandates(Acme), signed.Header.Id), EnvelopeWriter.Serialize(signed));
        return signed;
    }

    private BusRepository Repository(MachineName me)
    {
        var git = new GitClient(_processes, new GitClientOptions { Identity = new GitIdentity("zyggy (" + me.Value + ")", me.Value + "@zyggy.org") }, _clock);
        return new BusRepository(git, _provider, new BusIdentity(Acme, me), _checkout.Root, new EnvelopeSigner(_store), _clock, () => 0.5, new RecordingLogger<BusRepository>());
    }

    private BusInventory Inventory(MachineName me) =>
        new(Repository(me), _checkout.Root, new EnvelopeSigner(_store), _clock, new RecordingLogger<BusInventory>());

    private MandateGranter Granter(MachineName me)
    {
        BusRepository bus = Repository(me);
        var signer = new EnvelopeSigner(_store);
        return new MandateGranter(bus, new BusInventory(bus, _checkout.Root, signer, _clock, new RecordingLogger<BusInventory>()), signer, TestKeys.Acme1, _clock, new RecordingLogger<MandateGranter>());
    }
}
