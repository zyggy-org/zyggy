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

    private static readonly MachineName WorkLaptop = MachineName.Parse("work-laptop");

    private static readonly DateTimeOffset T = new(2026, 11, 16, 10, 0, 0, TimeSpan.Zero);

    private static readonly string[] AskedForTheBranch = ["ls-remote --exit-code origin refs/heads/feature/12-booking-pool"];

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

    // Spec 59 AC-6: a branch the origin does not have is refused when Central holds a checkout of the project.
    [Fact]
    public async Task GrantAsync_BranchAbsentOnTheOriginOfALocalCheckout_RefusedBranches_NothingWritten()
    {
        // Arrange
        _processes.On("ls-remote", RecordingProcessRunner.Fail(2, ""));

        // Act
        AskDecideResult result = await Granter(Central, LocalCalizr).GrantAsync(HomeLaptop, Fields(), TestContext.Current.CancellationToken);

        // Assert
        new { Result = result, Commits = _processes.CallsOf("commit").Count() }.Should().BeEquivalentTo(new
        {
            Result = new AskDecideRefused("branches", "'feature/12-booking-pool' does not exist on the origin of calizr"),
            Commits = 0,
        });
    }

    // Spec 59 AC-6: the check asks the origin of the local checkout for exactly the branch.
    [Fact]
    public async Task GrantAsync_LocalCheckout_AsksItsOriginForTheBranch_AndPublishesWhenPresent()
    {
        // Arrange
        _processes.On("ls-remote", RecordingProcessRunner.Ok("abc\trefs/heads/feature/12-booking-pool\n"));

        // Act
        AskDecideResult result = await Granter(Central, LocalCalizr).GrantAsync(HomeLaptop, Fields(), TestContext.Current.CancellationToken);

        // Assert
        new { Published = result is AskDecidePublished, Asked = _processes.CallsOf("ls-remote").Select(c => string.Join(' ', c.Arguments)).ToList() }
            .Should().BeEquivalentTo(new { Published = true, Asked = AskedForTheBranch });
    }

    // Spec 59 AC-6 (Q5): Central without the project sends; the node's report is the safety net.
    [Fact]
    public async Task GrantAsync_NoLocalCheckout_NoOriginAsked_Published()
    {
        // Arrange
        _processes.On("ls-remote", RecordingProcessRunner.Fail(2, ""));

        // Act
        AskDecideResult result = await Granter(Central, _ => null).GrantAsync(HomeLaptop, Fields(), TestContext.Current.CancellationToken);

        // Assert
        new { Published = result is AskDecidePublished, Asked = _processes.CallsOf("ls-remote").Count() }.Should().BeEquivalentTo(new { Published = true, Asked = 0 });
    }

    // Q23 (Central, 2026-11-22), narrowed by the owner 2026-11-23: a mandate for a work machine whose allowed_tools explicitly grant the
    // outlook verbs beside a code-running tool is refused at grant, with the runner's reason, before anything is signed or written.
    [Theory]
    [InlineData("Bash(zyggy outlook *)", "Bash(python *)")]
    [InlineData("Bash(zyggy outlook list *)", "Edit")]
    public async Task GrantAsync_WorkMachineCeilingMixes_RefusedWorkMailCodeMix_NothingWritten(string mail, string code)
    {
        // Act
        AskDecideResult result = await Granter(Central, workMachines: [WorkLaptop])
            .GrantAsync(WorkLaptop, Fields() with { AllowedTools = ["Read", mail, code] }, TestContext.Current.CancellationToken);

        // Assert
        new { Result = result, Calls = _processes.Calls.Count, Files = _checkout.FilesUnder(BusPaths.Mandates(Acme)).Count }.Should().BeEquivalentTo(new
        {
            Result = new AskDecideRefused("work_mail_code_mix", "allowed_tools can read work mail (" + mail + ") and run code (" + code + ") on a work machine"),
            Calls = 0,
            Files = 0,
        });
    }

    // Q23: the same ceiling for a machine that is not a work machine, and a work machine's mandate that reads no mail, are granted.
    [Theory]
    [InlineData("home-laptop", "Bash(zyggy outlook *)")]
    [InlineData("work-laptop", "Edit")]
    [InlineData("work-laptop", "Bash")]
    [InlineData("work-laptop", "mcp__github")]
    public async Task GrantAsync_NoWorkMailMixOnAWorkMachine_Granted(string to, string tool)
    {
        // Act
        AskDecideResult result = await Granter(Central, workMachines: [WorkLaptop])
            .GrantAsync(MachineName.Parse(to), Fields() with { AllowedTools = ["Read", tool, "Bash(python *)"] }, TestContext.Current.CancellationToken);

        // Assert
        result.Should().BeOfType<AskDecidePublished>();
    }

    private string? LocalCalizr(string project) => project == "calizr" ? Path.Combine(_checkout.Root, "calizr") : null;

    private MandateGranter Granter(MachineName me, Func<string, string?>? localProject = null, IReadOnlyList<MachineName>? workMachines = null)
    {
        BusRepository bus = Repository(me);
        var signer = new EnvelopeSigner(_store);
        var git = new GitClient(_processes, new GitClientOptions { Identity = new GitIdentity("zyggy (" + me.Value + ")", me.Value + "@zyggy.org") }, _clock);
        return new MandateGranter(
            bus,
            new BusInventory(bus, _checkout.Root, signer, _clock, new RecordingLogger<BusInventory>()),
            signer,
            TestKeys.Acme1,
            _clock,
            new RecordingLogger<MandateGranter>(),
            localProject is null ? null : new OriginBranchCheck(git, (name, _) => localProject(name)),
            workMachines ?? []);
    }
}
