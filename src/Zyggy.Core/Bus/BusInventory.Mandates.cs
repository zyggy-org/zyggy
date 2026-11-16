using Zyggy.Core.Envelope;
using Zyggy.Core.Runs;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Bus;

/// <content>The read side of the mandate check (deliverable 18): what this checkout says about one mandate.</content>
public sealed partial class BusInventory
{
    private const string UsageLimitBody = "claude_error: rate_limit";

    /// <summary>
    /// Reads everything this checkout holds about the mandate <paramref name="id"/>: its file in <c>mandates/</c> or
    /// <c>mandates/revoked/</c>, the verified jobs carrying its id in every machine's <c>jobs/</c> and <c>jobs/claimed/</c>, the verified
    /// reports carrying its id in every <c>reports/</c> folder and the archive, and the newest <c>resume</c> decision on it. A file that
    /// does not verify is never counted. Central and a node read the same way, each from its own checkout.
    /// </summary>
    /// <param name="id">The mandate's id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The ledger; <see cref="MandateLedger.FilePresent"/> is <see langword="false"/> when no such mandate is on the bus.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="id"/> is null.</exception>
    /// <exception cref="BusBusyException">Another zyggy process held the checkout's write lock for the whole wait.</exception>
    public async Task<MandateLedger> MandateLedgerAsync(EnvelopeId id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(id);
        using IDisposable handle = await LockAsync(ct).ConfigureAwait(false);
        string live = BusPaths.EnvelopeFile(BusPaths.Mandates(_me.Tenant), id);
        string revokedPath = BusPaths.EnvelopeFile(BusPaths.RevokedMandates(_me.Tenant), id);
        bool revoked = File.Exists(BusPaths.Resolve(_root, revokedPath));
        bool present = revoked || File.Exists(BusPaths.Resolve(_root, live));
        Envelope.Envelope? mandate = null;
        if (present)
        {
            (_, Envelope.Envelope? verified) = await SummariseAsync(revoked ? revokedPath : live, id, ct).ConfigureAwait(false);
            mandate = verified is { Type: EnvelopeType.Mandate } && verified.Header.Id == id && verified.Header.From == MachineName.Central ? verified : null;
        }

        var jobs = new List<MandateJobEntry>();
        var reports = new List<MandateReportEntry>();
        var reportFiles = new List<(string Path, EnvelopeId Id)>();
        foreach (MachineName machine in Machines())
        {
            foreach (string folder in new[] { BusPaths.Jobs(_me.Tenant, machine), BusPaths.Claimed(_me.Tenant, machine) })
            {
                foreach ((string path, EnvelopeId jobId) in Envelopes(folder))
                {
                    (_, Envelope.Envelope? job) = await SummariseAsync(path, jobId, ct).ConfigureAwait(false);
                    if (job?.Job is { } fields && fields.Mandate == id && job.Header.Id == jobId)
                    {
                        jobs.Add(new MandateJobEntry(jobId, job.Header.Created, fields.Step));
                    }
                }
            }

            reportFiles.AddRange(Envelopes(BusPaths.Reports(_me.Tenant, machine)));
        }

        foreach (string month in ArchiveMonths.Folders(_root, _me.Tenant))
        {
            reportFiles.AddRange(Envelopes(month));
        }

        foreach ((string path, EnvelopeId reportId) in reportFiles)
        {
            if (EnvelopeParser.PeekHeader(await File.ReadAllBytesAsync(BusPaths.Resolve(_root, path), ct).ConfigureAwait(false))?.Type != EnvelopeType.Report)
            {
                continue;
            }

            (_, Envelope.Envelope? report) = await SummariseAsync(path, reportId, ct).ConfigureAwait(false);
            if (report?.Report is { } fields && fields.Mandate == id && report.Header.Id == reportId && report.Header.InReplyTo is { } job)
            {
                bool usageLimit = fields.Status == ReportStatus.Failed
                    && fields.Reason == RunFailureReasonWire.ToWire(RunFailureReason.ClaudeError)
                    && report.BodyText.StartsWith(UsageLimitBody, StringComparison.Ordinal);
                reports.Add(new MandateReportEntry(reportId, job, report.Header.Created, fields.Status, fields.Reason, fields.Step, usageLimit));
            }
        }

        DateTimeOffset? resumed = null;
        foreach ((string path, EnvelopeId decisionId, bool archived) in DecisionFiles())
        {
            if (await ReadDecisionAsync(path, decisionId, archived, ct).ConfigureAwait(false) is { Fields.Kind: DecisionKind.Resume } decision
                && decision.InReplyTo == id
                && (resumed is null || decision.Created > resumed))
            {
                resumed = decision.Created;
            }
        }

        return new MandateLedger(
            id,
            mandate,
            present,
            revoked,
            [.. jobs.OrderBy(j => j.Id.Value, StringComparer.Ordinal)],
            [.. reports.OrderBy(r => r.Id.Value, StringComparer.Ordinal)],
            resumed);
    }
}
