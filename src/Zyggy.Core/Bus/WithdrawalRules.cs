using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Bus;

/// <summary>What the withdrawal write rule says about one request (deliverable 61).</summary>
internal enum WithdrawalVerdict
{
    /// <summary>The request is inside the rule.</summary>
    Allowed,

    /// <summary>The job, a path or the withdrawal belongs to another tenant than the machine's.</summary>
    OtherTenant,

    /// <summary>The machine (or the withdrawal's writer) is neither the job's sender nor Central.</summary>
    NotSender,

    /// <summary>The job's file is not where this move applies: <c>jobs/</c> to remove it, <c>jobs/claimed/</c> to write a withdrawal.</summary>
    NotWithdrawable,

    /// <summary>A claimed session task (<c>deliver: session</c>): finished with <c>zyggy work --failed</c>, never withdrawn.</summary>
    SessionTask,

    /// <summary>The path or the withdrawal is not the job's own: another file, another job, unsigned, or not this machine's.</summary>
    NotTheJob,

    /// <summary>The withdrawal's file is not in the claiming machine's <c>withdrawals/</c> under its own id.</summary>
    WrongPath,

    /// <summary>The withdrawal is addressed to another machine than this node.</summary>
    WrongRecipient,

    /// <summary>No job of the id is in this node's <c>jobs/</c> or <c>jobs/claimed/</c>: nothing to stop.</summary>
    UnknownJob,

    /// <summary>The withdrawal was written before the job it names: a replay against a job re-submitted under the same id.</summary>
    BeforeTheJob,
}

/// <summary>
/// The withdrawal branch of the §4 write rule (deliverable 61, Q1 (b)): a sender may remove its own queued job and write a
/// <c>withdrawal</c> for its own claimed job; Central may do both for any job of its tenant; a node acts on a verified withdrawal
/// only from that job's sender or Central. Pure: it reads envelopes and paths, never git, the clock or a key — the caller hands in
/// envelopes it has verified (signature, tenant, key) and the paths it read them at.
/// </summary>
internal static class WithdrawalRules
{
    /// <summary>
    /// Whether <paramref name="me"/> may remove the queued <paramref name="job"/> at <paramref name="jobPath"/>, in the commit that
    /// adds its own <c>failed</c> / <c>withdrawn</c> report: the job's own file in its target's <c>jobs/</c>, of this tenant, sent by
    /// <paramref name="me"/> or withdrawn by Central.
    /// </summary>
    /// <param name="me">The withdrawing machine.</param>
    /// <param name="job">The verified job read at <paramref name="jobPath"/>.</param>
    /// <param name="jobPath">Where the job sits.</param>
    /// <returns><see cref="WithdrawalVerdict.Allowed"/>, or the first rule broken.</returns>
    internal static WithdrawalVerdict MayRemoveQueued(BusIdentity me, Model job, string jobPath)
    {
        ArgumentNullException.ThrowIfNull(me);
        ArgumentNullException.ThrowIfNull(job);
        return JobAt(me, job, jobPath, BusFolder.Jobs, out _) is { } refused ? refused : Sender(me.Machine, job);
    }

    /// <summary>
    /// Whether <paramref name="me"/> may write <paramref name="withdrawal"/> at <paramref name="withdrawalPath"/> for the claimed
    /// <paramref name="job"/> at <paramref name="jobPath"/>: the job's own file in <c>jobs/claimed/</c>, not a session task, sent by
    /// <paramref name="me"/> or withdrawn by Central; the withdrawal signed by <paramref name="me"/>, naming the job and the claiming
    /// machine, in that machine's <c>withdrawals/</c> under its own id.
    /// </summary>
    /// <param name="me">The withdrawing machine.</param>
    /// <param name="job">The verified job read at <paramref name="jobPath"/>.</param>
    /// <param name="jobPath">Where the job sits.</param>
    /// <param name="withdrawal">The signed withdrawal about to be written.</param>
    /// <param name="withdrawalPath">Where it is about to be written.</param>
    /// <returns><see cref="WithdrawalVerdict.Allowed"/>, or the first rule broken.</returns>
    internal static WithdrawalVerdict MayWriteWithdrawal(BusIdentity me, Model job, string jobPath, Model withdrawal, string withdrawalPath)
    {
        ArgumentNullException.ThrowIfNull(me);
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(withdrawal);
        if (!BusPaths.TryParse(withdrawalPath, out BusLocation? at))
        {
            return WithdrawalVerdict.WrongPath;
        }

        if (at.Tenant != me.Tenant || withdrawal.Header.Tenant != me.Tenant)
        {
            return WithdrawalVerdict.OtherTenant;
        }

        if (JobAt(me, job, jobPath, BusFolder.Claimed, out MachineName? claimedBy) is { } refused)
        {
            return refused;
        }

        if (job.Job!.Deliver == JobDelivery.Session)
        {
            return WithdrawalVerdict.SessionTask;
        }

        if (Sender(me.Machine, job) is { } notSender and not WithdrawalVerdict.Allowed)
        {
            return notSender;
        }

        if (!IsSignedWithdrawal(withdrawal) || withdrawal.Header.From != me.Machine || withdrawal.Header.InReplyTo != job.Header.Id || withdrawal.Header.To != claimedBy)
        {
            return WithdrawalVerdict.NotTheJob;
        }

        return at.Folder == BusFolder.Withdrawals && at.Machine == claimedBy && at.Id == withdrawal.Header.Id
            ? WithdrawalVerdict.Allowed
            : WithdrawalVerdict.WrongPath;
    }

    /// <summary>
    /// Whether the node <paramref name="me"/> acts on the verified <paramref name="withdrawal"/> read at
    /// <paramref name="withdrawalPath"/>: in its own <c>withdrawals/</c> under its own id, addressed to it, naming
    /// <paramref name="job"/> (the job of that id in its <c>jobs/</c> or <c>jobs/claimed/</c>), written by that job's sender or
    /// Central, and not older than the job — an old withdrawal replayed against a job re-submitted under the same id is refused. A
    /// re-queued copy keeps the job's <c>created</c>, so a withdrawal still stops it.
    /// </summary>
    /// <param name="me">This node.</param>
    /// <param name="withdrawal">The withdrawal, verified by the caller.</param>
    /// <param name="withdrawalPath">Where it was read.</param>
    /// <param name="job">The verified job of the id the withdrawal names; <see langword="null"/> when this node holds none.</param>
    /// <returns><see cref="WithdrawalVerdict.Allowed"/>, or the first rule broken.</returns>
    internal static WithdrawalVerdict Accept(BusIdentity me, Model withdrawal, string withdrawalPath, Model? job)
    {
        ArgumentNullException.ThrowIfNull(me);
        ArgumentNullException.ThrowIfNull(withdrawal);
        if (withdrawal.Header.Tenant != me.Tenant)
        {
            return WithdrawalVerdict.OtherTenant;
        }

        if (!BusPaths.TryParse(withdrawalPath, out BusLocation? at))
        {
            return WithdrawalVerdict.WrongPath;
        }

        if (at.Tenant != me.Tenant)
        {
            return WithdrawalVerdict.OtherTenant;
        }

        if (!IsSignedWithdrawal(withdrawal))
        {
            return WithdrawalVerdict.NotTheJob;
        }

        if (at.Folder != BusFolder.Withdrawals || at.Machine != me.Machine || at.Id != withdrawal.Header.Id)
        {
            return WithdrawalVerdict.WrongPath;
        }

        if (withdrawal.Header.To != me.Machine)
        {
            return WithdrawalVerdict.WrongRecipient;
        }

        if (job is null)
        {
            return WithdrawalVerdict.UnknownJob;
        }

        if (job.Type != EnvelopeType.Job || job.Header.Tenant != me.Tenant || job.Header.Id != withdrawal.Header.InReplyTo || job.Header.To != me.Machine)
        {
            return WithdrawalVerdict.NotTheJob;
        }

        if (Sender(withdrawal.Header.From, job) is { } notSender and not WithdrawalVerdict.Allowed)
        {
            return notSender;
        }

        return withdrawal.Header.Created < job.Header.Created ? WithdrawalVerdict.BeforeTheJob : WithdrawalVerdict.Allowed;
    }

    // The job's own file in its target's folder of this tenant: the path parses, names the job's id and target, and is in the folder.
    private static WithdrawalVerdict? JobAt(BusIdentity me, Model job, string jobPath, BusFolder folder, out MachineName? target)
    {
        target = null;
        if (!BusPaths.TryParse(jobPath, out BusLocation? at))
        {
            return WithdrawalVerdict.NotTheJob;
        }

        if (at.Tenant != me.Tenant || job.Header.Tenant != me.Tenant)
        {
            return WithdrawalVerdict.OtherTenant;
        }

        if (at.Folder != folder)
        {
            return WithdrawalVerdict.NotWithdrawable;
        }

        if (job.Type != EnvelopeType.Job || job.Job is null || at.Id != job.Header.Id || at.Machine != job.Header.To)
        {
            return WithdrawalVerdict.NotTheJob;
        }

        target = at.Machine;
        return null;
    }

    // Q1 (b): the job's sender, or Central for any job of its tenant.
    private static WithdrawalVerdict Sender(MachineName writer, Model job) =>
        writer == job.Header.From || writer == MachineName.Central ? WithdrawalVerdict.Allowed : WithdrawalVerdict.NotSender;

    private static bool IsSignedWithdrawal(Model withdrawal) =>
        withdrawal.Type == EnvelopeType.Withdrawal && withdrawal.KeyId is not null && withdrawal.Signature is not null && withdrawal.Header.InReplyTo is not null;
}
