using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Bus;

/// <summary>
/// Spec 61 slice 4, AC-14: the write rule of a withdrawal. The sender may remove its own queued job and write a withdrawal for its
/// own claimed job; Central may do both for any job of its tenant; another machine and another tenant are refused. On the node, a
/// verified withdrawal is acted on only when it sits in this node's own <c>withdrawals/</c>, is addressed to it, names a job it holds,
/// comes from that job's sender or Central, and is not older than the job (a replay against a re-submitted id).
/// </summary>
public sealed class WithdrawalRulesTests
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly TenantId Globex = TenantId.Parse("globex");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly MachineName WorkLaptop = MachineName.Parse("work-laptop");

    private static readonly MachineName OtherLaptop = MachineName.Parse("other-laptop");

    private static readonly EnvelopeId JobId = EnvelopeId.Parse("01M87D0000JJJJJJJJJJJJJJJJ");

    private static readonly EnvelopeId WithdrawalId = EnvelopeId.Parse("01M87D0000WWWWWWWWWWWWWWWW");

    private static readonly DateTimeOffset JobCreated = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);

    // ---- sender side: removing a queued job (Behavior §1, queued) ----

    // AC-14: the job's sender removes its own queued job.
    [Fact]
    public async Task MayRemoveQueued_Sender_Allowed()
    {
        // Arrange
        Model job = await JobAsync(from: HomeLaptop);

        // Act
        WithdrawalVerdict verdict = WithdrawalRules.MayRemoveQueued(new BusIdentity(Acme, HomeLaptop), job, QueuedPath(Acme));

        // Assert
        verdict.Should().Be(WithdrawalVerdict.Allowed);
    }

    // AC-14 (Q1 (b)): Central removes any queued job of its tenant.
    [Fact]
    public async Task MayRemoveQueued_CentralForAnotherSendersJob_Allowed()
    {
        // Arrange
        Model job = await JobAsync(from: HomeLaptop);

        // Act
        WithdrawalVerdict verdict = WithdrawalRules.MayRemoveQueued(new BusIdentity(Acme, MachineName.Central), job, QueuedPath(Acme));

        // Assert
        verdict.Should().Be(WithdrawalVerdict.Allowed);
    }

    // AC-14: another machine (not the sender, not Central) is refused — also the target itself.
    [Theory]
    [InlineData("other-laptop")]
    [InlineData("work-laptop")]
    public async Task MayRemoveQueued_AnotherMachine_NotSender(string machine)
    {
        // Arrange
        Model job = await JobAsync(from: HomeLaptop);

        // Act
        WithdrawalVerdict verdict = WithdrawalRules.MayRemoveQueued(new BusIdentity(Acme, MachineName.Parse(machine)), job, QueuedPath(Acme));

        // Assert
        verdict.Should().Be(WithdrawalVerdict.NotSender);
    }

    // AC-14: another tenant's Central is refused, by the path and by the job.
    [Fact]
    public async Task MayRemoveQueued_AnotherTenantsCentral_OtherTenant()
    {
        // Arrange
        Model job = await JobAsync(from: HomeLaptop);

        // Act
        WithdrawalVerdict verdict = WithdrawalRules.MayRemoveQueued(new BusIdentity(Globex, MachineName.Central), job, QueuedPath(Acme));

        // Assert
        verdict.Should().Be(WithdrawalVerdict.OtherTenant);
    }

    // AC-14: only a file in a machine's jobs/ is removed this way; claimed, rejected or a report folder are not.
    [Theory]
    [InlineData("jobs/claimed")]
    [InlineData("jobs/rejected")]
    [InlineData("reports")]
    public async Task MayRemoveQueued_NotInJobs_NotWithdrawable(string folder)
    {
        // Arrange
        Model job = await JobAsync(from: HomeLaptop);
        string path = "tenants/acme/nodes/work-laptop/" + folder + "/" + JobId.Value + ".md";

        // Act
        WithdrawalVerdict verdict = WithdrawalRules.MayRemoveQueued(new BusIdentity(Acme, HomeLaptop), job, path);

        // Assert
        verdict.Should().Be(WithdrawalVerdict.NotWithdrawable);
    }

    // AC-14: the path must be the job's own file in its target's inbox.
    [Theory]
    [InlineData("tenants/acme/nodes/work-laptop/jobs/01M87D0000KKKKKKKKKKKKKKKK.md")]
    [InlineData("tenants/acme/nodes/other-laptop/jobs/01M87D0000JJJJJJJJJJJJJJJJ.md")]
    public async Task MayRemoveQueued_PathOfAnotherFile_NotTheJob(string path)
    {
        // Arrange
        Model job = await JobAsync(from: HomeLaptop);

        // Act
        WithdrawalVerdict verdict = WithdrawalRules.MayRemoveQueued(new BusIdentity(Acme, HomeLaptop), job, path);

        // Assert
        verdict.Should().Be(WithdrawalVerdict.NotTheJob);
    }

    // ---- sender side: writing a withdrawal for a claimed job (Behavior §1, claimed) ----

    // AC-14: the sender writes a withdrawal for its own claimed job, into the claiming machine's withdrawals/.
    [Fact]
    public async Task MayWriteWithdrawal_Sender_Allowed()
    {
        // Arrange
        Model job = await JobAsync(from: HomeLaptop);
        Model withdrawal = await WithdrawalAsync(from: HomeLaptop);

        // Act
        WithdrawalVerdict verdict = WithdrawalRules.MayWriteWithdrawal(new BusIdentity(Acme, HomeLaptop), job, ClaimedPath(Acme), withdrawal, WithdrawalPath(Acme, WorkLaptop));

        // Assert
        verdict.Should().Be(WithdrawalVerdict.Allowed);
    }

    // AC-14 (Q1 (b)): Central writes a withdrawal for any claimed job of its tenant.
    [Fact]
    public async Task MayWriteWithdrawal_CentralForAnotherSendersJob_Allowed()
    {
        // Arrange
        Model job = await JobAsync(from: HomeLaptop);
        Model withdrawal = await WithdrawalAsync(from: MachineName.Central);

        // Act
        WithdrawalVerdict verdict = WithdrawalRules.MayWriteWithdrawal(new BusIdentity(Acme, MachineName.Central), job, ClaimedPath(Acme), withdrawal, WithdrawalPath(Acme, WorkLaptop));

        // Assert
        verdict.Should().Be(WithdrawalVerdict.Allowed);
    }

    // AC-14: another machine may not write a withdrawal for a job it did not send.
    [Fact]
    public async Task MayWriteWithdrawal_AnotherMachine_NotSender()
    {
        // Arrange
        Model job = await JobAsync(from: HomeLaptop);
        Model withdrawal = await WithdrawalAsync(from: OtherLaptop);

        // Act
        WithdrawalVerdict verdict = WithdrawalRules.MayWriteWithdrawal(new BusIdentity(Acme, OtherLaptop), job, ClaimedPath(Acme), withdrawal, WithdrawalPath(Acme, WorkLaptop));

        // Assert
        verdict.Should().Be(WithdrawalVerdict.NotSender);
    }

    // AC-14: another tenant's machine is refused.
    [Fact]
    public async Task MayWriteWithdrawal_AnotherTenant_OtherTenant()
    {
        // Arrange
        Model job = await JobAsync(from: HomeLaptop);
        Model withdrawal = await WithdrawalAsync(from: MachineName.Central);

        // Act
        WithdrawalVerdict verdict = WithdrawalRules.MayWriteWithdrawal(new BusIdentity(Globex, MachineName.Central), job, ClaimedPath(Acme), withdrawal, WithdrawalPath(Acme, WorkLaptop));

        // Assert
        verdict.Should().Be(WithdrawalVerdict.OtherTenant);
    }

    // AC-14: the withdrawal goes to the claiming machine's own withdrawals/ — never another machine's, never another tenant's.
    [Theory]
    [InlineData("tenants/acme/nodes/other-laptop/withdrawals/01M87D0000WWWWWWWWWWWWWWWW.md")]
    [InlineData("tenants/acme/nodes/work-laptop/jobs/01M87D0000WWWWWWWWWWWWWWWW.md")]
    [InlineData("tenants/acme/nodes/work-laptop/withdrawals/01M87D0000KKKKKKKKKKKKKKKK.md")]
    public async Task MayWriteWithdrawal_WrongPath_WrongPath(string path)
    {
        // Arrange
        Model job = await JobAsync(from: HomeLaptop);
        Model withdrawal = await WithdrawalAsync(from: HomeLaptop);

        // Act
        WithdrawalVerdict verdict = WithdrawalRules.MayWriteWithdrawal(new BusIdentity(Acme, HomeLaptop), job, ClaimedPath(Acme), withdrawal, path);

        // Assert
        verdict.Should().Be(WithdrawalVerdict.WrongPath);
    }

    // AC-14: a withdrawal path in another tenant is refused as another tenant.
    [Fact]
    public async Task MayWriteWithdrawal_PathInAnotherTenant_OtherTenant()
    {
        // Arrange
        Model job = await JobAsync(from: HomeLaptop);
        Model withdrawal = await WithdrawalAsync(from: HomeLaptop);

        // Act
        WithdrawalVerdict verdict = WithdrawalRules.MayWriteWithdrawal(new BusIdentity(Acme, HomeLaptop), job, ClaimedPath(Acme), withdrawal, WithdrawalPath(Globex, WorkLaptop));

        // Assert
        verdict.Should().Be(WithdrawalVerdict.OtherTenant);
    }

    // Behavior §1: a withdrawal is written only for a claimed job (a queued one is removed instead).
    [Fact]
    public async Task MayWriteWithdrawal_QueuedJob_NotWithdrawable()
    {
        // Arrange
        Model job = await JobAsync(from: HomeLaptop);
        Model withdrawal = await WithdrawalAsync(from: HomeLaptop);

        // Act
        WithdrawalVerdict verdict = WithdrawalRules.MayWriteWithdrawal(new BusIdentity(Acme, HomeLaptop), job, QueuedPath(Acme), withdrawal, WithdrawalPath(Acme, WorkLaptop));

        // Assert
        verdict.Should().Be(WithdrawalVerdict.NotWithdrawable);
    }

    // Behavior §1: a claimed session task is not withdrawn (it is finished with zyggy work --failed).
    [Fact]
    public async Task MayWriteWithdrawal_ClaimedSessionTask_SessionTask()
    {
        // Arrange
        Model job = await JobAsync(from: HomeLaptop, session: true);
        Model withdrawal = await WithdrawalAsync(from: HomeLaptop);

        // Act
        WithdrawalVerdict verdict = WithdrawalRules.MayWriteWithdrawal(new BusIdentity(Acme, HomeLaptop), job, ClaimedPath(Acme), withdrawal, WithdrawalPath(Acme, WorkLaptop));

        // Assert
        verdict.Should().Be(WithdrawalVerdict.SessionTask);
    }

    // Behavior §1: a queued session task is withdrawn like any job.
    [Fact]
    public async Task MayRemoveQueued_QueuedSessionTask_Allowed()
    {
        // Arrange
        Model job = await JobAsync(from: HomeLaptop, session: true);

        // Act
        WithdrawalVerdict verdict = WithdrawalRules.MayRemoveQueued(new BusIdentity(Acme, HomeLaptop), job, QueuedPath(Acme));

        // Assert
        verdict.Should().Be(WithdrawalVerdict.Allowed);
    }

    // AC-14: the withdrawal must be this machine's own, signed, and name the job and its claiming machine.
    [Theory]
    [InlineData("unsigned")]
    [InlineData("other job")]
    [InlineData("other recipient")]
    [InlineData("other writer")]
    public async Task MayWriteWithdrawal_EnvelopeNotForThisJob_NotTheJob(string fault)
    {
        // Arrange
        Model job = await JobAsync(from: HomeLaptop);
        Model withdrawal = fault switch
        {
            "unsigned" => Withdrawal(from: HomeLaptop),
            "other job" => await WithdrawalAsync(from: HomeLaptop, job: EnvelopeId.Parse("01M87D0000KKKKKKKKKKKKKKKK")),
            "other recipient" => await WithdrawalAsync(from: HomeLaptop, to: OtherLaptop),
            _ => await WithdrawalAsync(from: MachineName.Central),
        };

        // Act
        WithdrawalVerdict verdict = WithdrawalRules.MayWriteWithdrawal(new BusIdentity(Acme, HomeLaptop), job, ClaimedPath(Acme), withdrawal, WithdrawalPath(Acme, WorkLaptop));

        // Assert
        verdict.Should().Be(WithdrawalVerdict.NotTheJob);
    }

    // ---- node side: acting on a verified withdrawal (Behavior §1, "On the node") ----

    // AC-14 / AC-17: a withdrawal by the job's sender, addressed to this node, for a job it holds, is acted on.
    [Fact]
    public async Task Accept_SendersWithdrawalOfAHeldJob_Allowed()
    {
        // Arrange
        Model job = await JobAsync(from: HomeLaptop);
        Model withdrawal = await WithdrawalAsync(from: HomeLaptop);

        // Act
        WithdrawalVerdict verdict = WithdrawalRules.Accept(new BusIdentity(Acme, WorkLaptop), withdrawal, WithdrawalPath(Acme, WorkLaptop), job);

        // Assert
        verdict.Should().Be(WithdrawalVerdict.Allowed);
    }

    // AC-14 / AC-17 (Q1 (b)): Central's withdrawal of any job of its tenant is acted on.
    [Fact]
    public async Task Accept_CentralsWithdrawal_Allowed()
    {
        // Arrange
        Model job = await JobAsync(from: HomeLaptop);
        Model withdrawal = await WithdrawalAsync(from: MachineName.Central);

        // Act
        WithdrawalVerdict verdict = WithdrawalRules.Accept(new BusIdentity(Acme, WorkLaptop), withdrawal, WithdrawalPath(Acme, WorkLaptop), job);

        // Assert
        verdict.Should().Be(WithdrawalVerdict.Allowed);
    }

    // Task: a withdrawal signed by a node for a job it did not send is refused (left in place by the node).
    [Fact]
    public async Task Accept_NodeThatDidNotSendTheJob_NotSender()
    {
        // Arrange
        Model job = await JobAsync(from: HomeLaptop);
        Model withdrawal = await WithdrawalAsync(from: OtherLaptop);

        // Act
        WithdrawalVerdict verdict = WithdrawalRules.Accept(new BusIdentity(Acme, WorkLaptop), withdrawal, WithdrawalPath(Acme, WorkLaptop), job);

        // Assert
        verdict.Should().Be(WithdrawalVerdict.NotSender);
    }

    // Task: a withdrawal for an id this node holds no job of is not acted on (the node removes it as "nothing to stop").
    [Fact]
    public async Task Accept_UnknownId_UnknownJob()
    {
        // Arrange
        Model withdrawal = await WithdrawalAsync(from: HomeLaptop);

        // Act
        WithdrawalVerdict verdict = WithdrawalRules.Accept(new BusIdentity(Acme, WorkLaptop), withdrawal, WithdrawalPath(Acme, WorkLaptop), job: null);

        // Assert
        verdict.Should().Be(WithdrawalVerdict.UnknownJob);
    }

    // Task: the job handed in is not the one the withdrawal names.
    [Fact]
    public async Task Accept_AnotherJob_NotTheJob()
    {
        // Arrange
        Model job = await JobAsync(from: HomeLaptop);
        Model withdrawal = await WithdrawalAsync(from: HomeLaptop, job: EnvelopeId.Parse("01M87D0000KKKKKKKKKKKKKKKK"));

        // Act
        WithdrawalVerdict verdict = WithdrawalRules.Accept(new BusIdentity(Acme, WorkLaptop), withdrawal, WithdrawalPath(Acme, WorkLaptop), job);

        // Assert
        verdict.Should().Be(WithdrawalVerdict.NotTheJob);
    }

    // Task: an old withdrawal replayed against a job re-submitted later under the same id is refused — a withdrawal never predates the job it withdraws.
    [Fact]
    public async Task Accept_WithdrawalOlderThanTheJob_BeforeTheJob()
    {
        // Arrange
        Model resubmitted = await JobAsync(from: HomeLaptop, created: JobCreated.AddHours(3));
        Model replayed = await WithdrawalAsync(from: HomeLaptop, created: JobCreated.AddHours(1));

        // Act
        WithdrawalVerdict verdict = WithdrawalRules.Accept(new BusIdentity(Acme, WorkLaptop), replayed, WithdrawalPath(Acme, WorkLaptop), resubmitted);

        // Assert
        verdict.Should().Be(WithdrawalVerdict.BeforeTheJob);
    }

    // Spec 17 re-queue keeps `created`: a withdrawal written after the job still stops its re-queued copy (attempt 2).
    [Fact]
    public async Task Accept_RequeuedCopyOfTheJob_Allowed()
    {
        // Arrange
        Model requeued = await SignAsync((await JobAsync(from: HomeLaptop)).WithAttempt(2));
        Model withdrawal = await WithdrawalAsync(from: HomeLaptop);

        // Act
        WithdrawalVerdict verdict = WithdrawalRules.Accept(new BusIdentity(Acme, WorkLaptop), withdrawal, WithdrawalPath(Acme, WorkLaptop), requeued);

        // Assert
        verdict.Should().Be(WithdrawalVerdict.Allowed);
    }

    // AC-17: a withdrawal addressed to another machine, or sitting in another machine's folder, is not acted on.
    [Fact]
    public async Task Accept_AddressedToAnotherMachine_WrongRecipient()
    {
        // Arrange
        Model job = await JobAsync(from: HomeLaptop);
        Model withdrawal = await WithdrawalAsync(from: HomeLaptop, to: OtherLaptop);

        // Act
        WithdrawalVerdict verdict = WithdrawalRules.Accept(new BusIdentity(Acme, WorkLaptop), withdrawal, WithdrawalPath(Acme, WorkLaptop), job);

        // Assert
        verdict.Should().Be(WithdrawalVerdict.WrongRecipient);
    }

    // AC-17: only this node's own withdrawals/ folder is read, under the withdrawal's own id.
    [Theory]
    [InlineData("tenants/acme/nodes/other-laptop/withdrawals/01M87D0000WWWWWWWWWWWWWWWW.md")]
    [InlineData("tenants/acme/nodes/work-laptop/withdrawals/01M87D0000KKKKKKKKKKKKKKKK.md")]
    [InlineData("tenants/acme/nodes/work-laptop/jobs/01M87D0000WWWWWWWWWWWWWWWW.md")]
    public async Task Accept_NotInThisNodesWithdrawals_WrongPath(string path)
    {
        // Arrange
        Model job = await JobAsync(from: HomeLaptop);
        Model withdrawal = await WithdrawalAsync(from: HomeLaptop);

        // Act
        WithdrawalVerdict verdict = WithdrawalRules.Accept(new BusIdentity(Acme, WorkLaptop), withdrawal, path, job);

        // Assert
        verdict.Should().Be(WithdrawalVerdict.WrongPath);
    }

    // Task: a withdrawal of another tenant (its own tenant and key) is refused, whatever else it says.
    [Fact]
    public async Task Accept_WithdrawalOfAnotherTenant_OtherTenant()
    {
        // Arrange
        Model job = await JobAsync(from: HomeLaptop);
        Model foreign = Withdrawal(from: MachineName.Central, tenant: Globex);

        // Act
        WithdrawalVerdict verdict = WithdrawalRules.Accept(new BusIdentity(Acme, WorkLaptop), foreign, WithdrawalPath(Acme, WorkLaptop), job);

        // Assert
        verdict.Should().Be(WithdrawalVerdict.OtherTenant);
    }

    // AC-17: an unsigned withdrawal is never acted on (the caller hands in only verified envelopes; this is the second fence).
    [Fact]
    public async Task Accept_Unsigned_NotTheJob()
    {
        // Arrange
        Model job = await JobAsync(from: HomeLaptop);

        // Act
        WithdrawalVerdict verdict = WithdrawalRules.Accept(new BusIdentity(Acme, WorkLaptop), Withdrawal(from: HomeLaptop), WithdrawalPath(Acme, WorkLaptop), job);

        // Assert
        verdict.Should().Be(WithdrawalVerdict.NotTheJob);
    }

    // ---- BusPaths: the new folder ----

    // Spec 61 §4 layout: nodes/<machine>/withdrawals/ is built by BusPaths and read back.
    [Fact]
    public void BusPaths_Withdrawals_BuildsAndParses()
    {
        // Act
        string folder = BusPaths.Withdrawals(Acme, WorkLaptop);
        bool ok = BusPaths.TryParse(BusPaths.EnvelopeFile(folder, WithdrawalId), out BusLocation? location);

        // Assert
        (folder, ok, location).Should().Be(("tenants/acme/nodes/work-laptop/withdrawals", true, new BusLocation(Acme, WorkLaptop, BusFolder.Withdrawals, WithdrawalId)));
    }

    private static string QueuedPath(TenantId tenant) => BusPaths.EnvelopeFile(BusPaths.Jobs(tenant, WorkLaptop), JobId);

    private static string ClaimedPath(TenantId tenant) => BusPaths.EnvelopeFile(BusPaths.Claimed(tenant, WorkLaptop), JobId);

    private static string WithdrawalPath(TenantId tenant, MachineName machine) => BusPaths.EnvelopeFile(BusPaths.Withdrawals(tenant, machine), WithdrawalId);

    private static async Task<Model> JobAsync(MachineName from, bool session = false, DateTimeOffset? created = null)
    {
        var header = new EnvelopeHeader(Acme, JobId, from, WorkLaptop, created ?? JobCreated);
        var fields = new JobFields("shop", AllowedTools: ["Read"], Deliver: session ? JobDelivery.Session : JobDelivery.Run);
        return await SignAsync(Model.CreateJob(header, fields, "Build slice 1.\n"u8.ToArray()));
    }

    private static Model Withdrawal(MachineName from, MachineName? to = null, EnvelopeId? job = null, DateTimeOffset? created = null, TenantId? tenant = null) =>
        Model.CreateWithdrawal(new EnvelopeHeader(tenant ?? Acme, WithdrawalId, from, to ?? WorkLaptop, created ?? JobCreated.AddHours(1), InReplyTo: job ?? JobId), why: null);

    private static async Task<Model> WithdrawalAsync(MachineName from, MachineName? to = null, EnvelopeId? job = null, DateTimeOffset? created = null) =>
        await SignAsync(Withdrawal(from, to, job, created));

    private static async Task<Model> SignAsync(Model envelope)
    {
        var ct = TestContext.Current.CancellationToken;
        var signer = new EnvelopeSigner(await TestKeys.StoreWithBothAsync(ct));
        return (await signer.SignAsync(envelope, TestKeys.Acme1, ct)).Envelope!;
    }
}
