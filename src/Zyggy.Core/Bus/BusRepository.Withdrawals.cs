using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Bus;

/// <content>Spec 61 slice 5: the bus moves of a withdrawal — the sender's two, and the node's "nothing to stop".</content>
public sealed partial class BusRepository
{
    /// <summary>
    /// The sender's lookup: every file named <c>&lt;id&gt;.md</c> in the <c>jobs/</c> or <c>jobs/claimed/</c> folder of any machine of this
    /// tenant, with its bytes, under the checkout's write lock. Never another tenant's subtree; nothing is verified here.
    /// </summary>
    /// <param name="id">The job id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The files found, machines in ordinal order, <c>jobs/</c> before <c>jobs/claimed/</c>.</returns>
    /// <exception cref="BusBusyException">Another zyggy process held the checkout's write lock for the whole wait.</exception>
    internal async Task<IReadOnlyList<(string Path, MachineName Machine, bool Claimed, byte[] Bytes)>> FindJobFilesAsync(EnvelopeId id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(id);
        using IDisposable handle = await BusCheckoutLock.AcquireAsync(_checkoutRoot, BusCheckoutLock.WriteLockName, LockTimeout, _clock, ct).ConfigureAwait(false)
            ?? throw new BusBusyException();
        string nodes = BusPaths.Resolve(_checkoutRoot, BusPaths.Nodes(_me.Tenant));
        var found = new List<(string Path, MachineName Machine, bool Claimed, byte[] Bytes)>();
        if (!Directory.Exists(nodes))
        {
            return found;
        }

        IEnumerable<MachineName> machines = Directory.EnumerateDirectories(nodes)
            .Select(d => MachineName.TryParse(Path.GetFileName(d), out MachineName? machine) ? machine : null)
            .OfType<MachineName>()
            .OrderBy(m => m.Value, StringComparer.Ordinal);
        foreach (MachineName machine in machines)
        {
            foreach ((string folder, bool claimed) in new[] { (BusPaths.Jobs(_me.Tenant, machine), false), (BusPaths.Claimed(_me.Tenant, machine), true) })
            {
                string path = BusPaths.EnvelopeFile(folder, id);
                string full = BusPaths.Resolve(_checkoutRoot, path);
                if (File.Exists(full))
                {
                    found.Add((path, machine, claimed, await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false)));
                }
            }
        }

        return found;
    }

    /// <summary>
    /// Spec 61 §1, queued: removes <paramref name="job"/>'s file from its target's <c>jobs/</c> and adds <paramref name="signedReport"/> (this
    /// machine's <c>failed</c> / <c>withdrawn</c> report) to this machine's own <c>reports/</c>, in one commit
    /// <c>&lt;tenant&gt; withdraw &lt;id&gt; &lt;me&gt;→&lt;target&gt;</c>. After the write's pull the file must still be the same bytes;
    /// otherwise nothing is written and the outcome is <c>WriteFailed("rm", …)</c> (the node claimed it meanwhile).
    /// </summary>
    /// <param name="jobPath">The job's file in its target's <c>jobs/</c>.</param>
    /// <param name="job">The verified job read there.</param>
    /// <param name="jobBytes">The bytes it was read with.</param>
    /// <param name="signedReport">This machine's signed report on the job.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The write outcome.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the move is outside the withdrawal write rule (a caller bug).</exception>
    internal Task<WriteOutcome> WithdrawQueuedAsync(string jobPath, Model job, byte[] jobBytes, Model signedReport, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(jobBytes);
        ArgumentNullException.ThrowIfNull(signedReport);
        if (WithdrawalRules.MayRemoveQueued(_me, job, jobPath) is not WithdrawalVerdict.Allowed and var refused)
        {
            throw new InvalidOperationException("write rule: '" + jobPath + "' may not be withdrawn by this machine (" + refused + ")");
        }

        if (signedReport.Type != EnvelopeType.Report
            || signedReport.Signature is null
            || signedReport.Header.From != _me.Machine
            || signedReport.Header.InReplyTo != job.Header.Id)
        {
            throw new InvalidOperationException("write rule: the report on " + jobPath + " is not a signed report of this machine");
        }

        string reportPath = BusPaths.EnvelopeFile(BusPaths.Reports(_me.Tenant, _me.Machine), signedReport.Header.Id);
        AssertMayWrite(reportPath, BusFolder.Reports);
        byte[] bytes = EnvelopeWriter.Serialize(signedReport);
        return WriteAsync(
            Subject("withdraw", job.Header.Id, _me.Machine.Value, job.Header.To.Value),
            [reportPath, jobPath],
            async token =>
            {
                string full = BusPaths.Resolve(_checkoutRoot, jobPath);
                if (!File.Exists(full) || !(await File.ReadAllBytesAsync(full, token).ConfigureAwait(false)).AsSpan().SequenceEqual(jobBytes))
                {
                    return new WriteFailed("rm", "the job's file is no longer queued");
                }

                await WriteFileAsync(reportPath, bytes, token).ConfigureAwait(false);
                return Failure("add", await _git.AddAsync(_checkoutRoot, [reportPath], token).ConfigureAwait(false))
                    ?? Failure("rm", await _git.RemoveAsync(_checkoutRoot, [jobPath], token).ConfigureAwait(false));
            },
            ct);
    }

    /// <summary>
    /// Spec 61 §1, claimed: adds <paramref name="signedWithdrawal"/> to the claiming machine's <c>withdrawals/</c> in one commit
    /// <c>&lt;tenant&gt; withdrawal &lt;job id&gt; &lt;me&gt;→&lt;target&gt;</c>, after the write rule allowed it for <paramref name="job"/>.
    /// </summary>
    /// <param name="jobPath">The job's file in its target's <c>jobs/claimed/</c>.</param>
    /// <param name="job">The verified job read there.</param>
    /// <param name="signedWithdrawal">The signed withdrawal.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The write outcome.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the write is outside the withdrawal write rule (a caller bug).</exception>
    internal Task<WriteOutcome> RequestWithdrawalAsync(string jobPath, Model job, Model signedWithdrawal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(signedWithdrawal);
        string path = BusPaths.EnvelopeFile(BusPaths.Withdrawals(_me.Tenant, signedWithdrawal.Header.To), signedWithdrawal.Header.Id);
        if (WithdrawalRules.MayWriteWithdrawal(_me, job, jobPath, signedWithdrawal, path) is not WithdrawalVerdict.Allowed and var refused)
        {
            throw new InvalidOperationException("write rule: the withdrawal of " + jobPath + " may not be written by this machine (" + refused + ")");
        }

        byte[] bytes = EnvelopeWriter.Serialize(signedWithdrawal);
        return WriteAsync(
            Subject("withdrawal", job.Header.Id, _me.Machine.Value, signedWithdrawal.Header.To.Value),
            [path],
            async token =>
            {
                await WriteFileAsync(path, bytes, token).ConfigureAwait(false);
                return Failure("add", await _git.AddAsync(_checkoutRoot, [path], token).ConfigureAwait(false));
            },
            ct);
    }

    /// <summary>This node's own <c>withdrawals/</c> files in ordinal order (spec 61 §1, "On the node").</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The files.</returns>
    internal Task<IReadOnlyList<BusFile>> ListWithdrawalsAsync(CancellationToken ct) => ListFolderAsync(BusPaths.Withdrawals(_me.Tenant, _me.Machine), ct);

    /// <summary>Reads and verifies one of this node's withdrawal files; the rejection when it does not verify.</summary>
    /// <param name="file">A file from <see cref="ListWithdrawalsAsync"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The verified envelope, or the reason it was refused.</returns>
    internal async Task<EnvelopeResult> VerifyWithdrawalAsync(BusFile file, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);
        byte[] bytes = await File.ReadAllBytesAsync(BusPaths.Resolve(_checkoutRoot, file.RelativePath), ct).ConfigureAwait(false);
        return await _signer.ParseAndVerifyAsync(bytes, _me.Tenant, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The job file of <paramref name="id"/> in this node's own <c>jobs/claimed/</c>, else its <c>jobs/</c>, verified as at intake; <see langword="null"/>
    /// when neither holds one, or the file does not verify.
    /// </summary>
    /// <param name="id">The job id a withdrawal names.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The verified job, or <see langword="null"/>.</returns>
    internal async Task<AcceptedJob?> FindOwnJobAsync(EnvelopeId id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(id);
        foreach (string folder in new[] { BusPaths.Claimed(_me.Tenant, _me.Machine), BusPaths.Jobs(_me.Tenant, _me.Machine) })
        {
            string path = BusPaths.EnvelopeFile(folder, id);
            if (File.Exists(BusPaths.Resolve(_checkoutRoot, path)))
            {
                return await VerifyIntakeAsync(new BusFile(path, id), ct).ConfigureAwait(false) as AcceptedJob;
            }
        }

        return null;
    }

    /// <summary>
    /// Spec 61 §1: removes a withdrawal whose job this node no longer holds, in one commit
    /// <c>&lt;tenant&gt; withdrawal &lt;job id&gt; &lt;me&gt;→&lt;from&gt; nothing to stop</c>; no report.
    /// </summary>
    /// <param name="withdrawal">The withdrawal file in this node's <c>withdrawals/</c>.</param>
    /// <param name="job">The job id it names.</param>
    /// <param name="from">Its sender.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The write outcome.</returns>
    internal Task<WriteOutcome> RemoveWithdrawalAsync(BusFile withdrawal, EnvelopeId job, MachineName from, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(withdrawal);
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(from);
        string path = withdrawal.RelativePath;
        AssertMayWrite(path, BusFolder.Withdrawals);
        return WriteAsync(
            Subject("withdrawal", job, _me.Machine.Value, from.Value) + " nothing to stop",
            [path],
            async token => File.Exists(BusPaths.Resolve(_checkoutRoot, path))
                ? Failure("rm", await _git.RemoveAsync(_checkoutRoot, [path], token).ConfigureAwait(false))
                : new WriteFailed("rm", "the withdrawal is gone"),
            ct);
    }
}
