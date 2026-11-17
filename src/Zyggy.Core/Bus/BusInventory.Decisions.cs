using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Bus;

/// <summary>One decision of Central as it stands on the bus (deliverable 18, "Questions and decisions").</summary>
/// <param name="Id">The decision's id.</param>
/// <param name="To">The machine concerned.</param>
/// <param name="Created">When Central wrote it.</param>
/// <param name="InReplyTo">The question, report, job or mandate it settles.</param>
/// <param name="Fields">Kind, basis, undo and the optional mandate, project, step and evidence.</param>
/// <param name="Text">What was decided and why.</param>
/// <param name="Archived"><see langword="true"/> when the file was read from <c>archive/</c>.</param>
public sealed record DecisionRecord(EnvelopeId Id, MachineName To, DateTimeOffset Created, EnvelopeId InReplyTo, DecisionFields Fields, string Text, bool Archived);

/// <summary>What a decision may reply to: an envelope found on the bus by its id (deliverable 18).</summary>
/// <param name="Machine">The machine the decision goes to: the asker of a question, the writer of a report, the receiver of a job.</param>
/// <param name="Project">The project of the question or job, when the envelope names one.</param>
/// <param name="Question">The verified question while it still sits in its <c>questions/</c> folder, so the decision can archive it; otherwise <see langword="null"/>.</param>
public sealed record DecisionTarget(MachineName Machine, string? Project, Envelope.Envelope? Question);

/// <content>The read side of <c>zyggy decide</c>, <c>zyggy decisions</c> and the wait of <c>zyggy ask</c> (deliverable 18).</content>
public sealed partial class BusInventory
{
    /// <summary>
    /// Central's decisions, newest first: the files of Central's <c>decisions/</c> folder and of every archive month that verify as a
    /// <c>decision</c> under their own id. Anything else is the auditor's business and is left out.
    /// </summary>
    /// <param name="since">Only decisions created at or after this instant; <see langword="null"/> for all.</param>
    /// <param name="mandate">Only decisions that act under this mandate; <see langword="null"/> for all.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The decisions.</returns>
    /// <exception cref="BusBusyException">Another zyggy process held the checkout's write lock for the whole wait.</exception>
    public async Task<IReadOnlyList<DecisionRecord>> DecisionsAsync(DateTimeOffset? since, EnvelopeId? mandate, CancellationToken ct)
    {
        using IDisposable handle = await LockAsync(ct).ConfigureAwait(false);
        var decisions = new List<DecisionRecord>();
        foreach ((string path, EnvelopeId id, bool archived) in DecisionFiles())
        {
            if (await ReadDecisionAsync(path, id, archived, ct).ConfigureAwait(false) is { } decision
                && (since is null || decision.Created >= since)
                && (mandate is null || decision.Fields.Mandate == mandate))
            {
                decisions.Add(decision);
            }
        }

        return [.. decisions.OrderByDescending(d => d.Id.Value, StringComparer.Ordinal)];
    }

    /// <summary>The newest decision whose <c>in_reply_to</c> is <paramref name="inReplyTo"/>, live or archived.</summary>
    /// <param name="inReplyTo">The id the decision settles.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The decision, or <see langword="null"/> when there is none.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="inReplyTo"/> is null.</exception>
    /// <exception cref="BusBusyException">Another zyggy process held the checkout's write lock for the whole wait.</exception>
    public async Task<DecisionRecord?> DecisionForAsync(EnvelopeId inReplyTo, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(inReplyTo);
        using IDisposable handle = await LockAsync(ct).ConfigureAwait(false);
        foreach ((string path, EnvelopeId id, bool archived) in DecisionFiles().OrderByDescending(f => f.Id.Value, StringComparer.Ordinal))
        {
            if (EnvelopeParser.PeekHeader(await File.ReadAllBytesAsync(BusPaths.Resolve(_root, path), ct).ConfigureAwait(false))?.InReplyTo == inReplyTo
                && await ReadDecisionAsync(path, id, archived, ct).ConfigureAwait(false) is { } decision)
            {
                return decision;
            }
        }

        return null;
    }

    /// <summary>
    /// Finds what a decision replies to: a question in a machine's <c>questions/</c> folder, a job in <c>jobs/</c>, <c>claimed/</c> or
    /// <c>rejected/</c>, a report in <c>reports/</c>, a mandate of Central (live or revoked), or a question or report already in the archive. Only a file that verifies under
    /// its own id and its folder's writer counts.
    /// </summary>
    /// <param name="id">The id to reply to.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Where the decision goes, or <see langword="null"/> when nothing on the bus has that id.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="id"/> is null.</exception>
    /// <exception cref="BusBusyException">Another zyggy process held the checkout's write lock for the whole wait.</exception>
    public async Task<DecisionTarget?> FindDecidableAsync(EnvelopeId id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(id);
        using IDisposable handle = await LockAsync(ct).ConfigureAwait(false);
        foreach (MachineName machine in Machines())
        {
            if (await VerifiedAtAsync(BusPaths.Questions(_me.Tenant, machine), id, ct).ConfigureAwait(false) is { Question: { } asked } question
                && question.Header.From == machine)
            {
                return new DecisionTarget(machine, asked.Project, question);
            }

            foreach (string folder in new[] { BusPaths.Jobs(_me.Tenant, machine), BusPaths.Claimed(_me.Tenant, machine), BusPaths.Rejected(_me.Tenant, machine) })
            {
                if (await VerifiedAtAsync(folder, id, ct).ConfigureAwait(false) is { Job: { } job } found && found.Header.To == machine)
                {
                    return new DecisionTarget(machine, job.Project, null);
                }
            }

            if (await VerifiedAtAsync(BusPaths.Reports(_me.Tenant, machine), id, ct).ConfigureAwait(false) is { Type: EnvelopeType.Report } report
                && report.Header.From == machine)
            {
                return new DecisionTarget(machine, null, null);
            }
        }

        // Deliverable 18 group B: a decision on a mandate (live or revoked) goes to the machine the mandate is for.
        foreach (string folder in new[] { BusPaths.Mandates(_me.Tenant), BusPaths.RevokedMandates(_me.Tenant) })
        {
            if (await VerifiedAtAsync(folder, id, ct).ConfigureAwait(false) is { Mandate: { } mandate } found && found.Header.From == MachineName.Central)
            {
                return new DecisionTarget(found.Header.To, mandate.Project, null);
            }
        }

        foreach (string month in ArchiveMonths.Folders(_root, _me.Tenant))
        {
            if (await VerifiedAtAsync(month, id, ct).ConfigureAwait(false) is { } archived && archived.Type is EnvelopeType.Question or EnvelopeType.Report)
            {
                return new DecisionTarget(archived.Header.From, archived.Question?.Project, null);
            }
        }

        return null;
    }

    /// <summary>Central's live decisions, then every archived file whose header says <c>decision</c>.</summary>
    private IEnumerable<(string Path, EnvelopeId Id, bool Archived)> DecisionFiles()
    {
        foreach ((string path, EnvelopeId id) in Envelopes(BusPaths.Decisions(_me.Tenant, MachineName.Central)))
        {
            yield return (path, id, false);
        }

        foreach (string month in ArchiveMonths.Folders(_root, _me.Tenant))
        {
            foreach ((string path, EnvelopeId id) in Envelopes(month))
            {
                if (EnvelopeParser.PeekHeader(File.ReadAllBytes(BusPaths.Resolve(_root, path)))?.Type == EnvelopeType.Decision)
                {
                    yield return (path, id, true);
                }
            }
        }
    }

    private async Task<DecisionRecord?> ReadDecisionAsync(string path, EnvelopeId id, bool archived, CancellationToken ct)
    {
        (_, Envelope.Envelope? verified) = await SummariseAsync(path, id, ct).ConfigureAwait(false);
        return verified is { Decision: { } fields, Header.InReplyTo: { } inReplyTo } && verified.Header.Id == id && verified.Header.From == MachineName.Central
            ? new DecisionRecord(id, verified.Header.To, verified.Header.Created, inReplyTo, fields, verified.BodyText, archived)
            : null;
    }

    /// <summary>The envelope <paramref name="id"/> in <paramref name="folder"/> when the file exists and verifies under that id.</summary>
    private async Task<Envelope.Envelope?> VerifiedAtAsync(string folder, EnvelopeId id, CancellationToken ct)
    {
        string path = BusPaths.EnvelopeFile(folder, id);
        if (!File.Exists(BusPaths.Resolve(_root, path)))
        {
            return null;
        }

        (_, Envelope.Envelope? verified) = await SummariseAsync(path, id, ct).ConfigureAwait(false);
        return verified is not null && verified.Header.Id == id ? verified : null;
    }
}
