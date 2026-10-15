using System.Text;

using Microsoft.Extensions.Logging;

using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Bus.Probe;

/// <summary>
/// The sender side of the transport probe (deliverable 04, <c>zyggy probe send</c>): pushes a signed <c>probe</c> job to a
/// machine, then rings the doorbell with its own poller state until that machine's verified report is on the bus or the
/// timeout elapses, and records the outcome in the ledger on the sender's clock only (O21). The job body is a fixed string.
/// </summary>
public sealed partial class ProbeSender
{
    /// <summary>The fixed job body; never work data (founding spec §8).</summary>
    public const string Body = "zyggy transport probe\n";

    private readonly IBusProvider _provider;
    private readonly BusRepository _bus;
    private readonly EnvelopeSigner _signer;
    private readonly BusIdentity _me;
    private readonly KeyId _signWith;
    private readonly PollOptions _options;
    private readonly TimeProvider _clock;
    private readonly Random _jitter;
    private readonly ProbeLedger _ledger;
    private readonly ILogger<ProbeSender> _log;

    /// <summary>Creates the sender.</summary>
    /// <param name="provider">The bus host seam.</param>
    /// <param name="bus">The repository layer for this node.</param>
    /// <param name="signer">The envelope signer.</param>
    /// <param name="me">This node's identity.</param>
    /// <param name="signWith">The key the probe is signed with.</param>
    /// <param name="options">The poller's intervals used while waiting for the report.</param>
    /// <param name="clock">The clock; the only time source.</param>
    /// <param name="jitter">The random source for the poll jitter.</param>
    /// <param name="ledger">The ledger every outcome is appended to.</param>
    /// <param name="log">The logger.</param>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    public ProbeSender(
        IBusProvider provider,
        BusRepository bus,
        EnvelopeSigner signer,
        BusIdentity me,
        KeyId signWith,
        PollOptions options,
        TimeProvider clock,
        Random jitter,
        ProbeLedger ledger,
        ILogger<ProbeSender> log)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        _me = me ?? throw new ArgumentNullException(nameof(me));
        _signWith = signWith ?? throw new ArgumentNullException(nameof(signWith));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _jitter = jitter ?? throw new ArgumentNullException(nameof(jitter));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>Sends one probe to <paramref name="to"/> and waits up to <paramref name="timeout"/> for its report.</summary>
    /// <param name="to">The target machine.</param>
    /// <param name="timeout">How long to wait for the report after the push.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The ledger record, already appended.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="to"/> is null.</exception>
    public async Task<ProbeRecord> SendOneAsync(MachineName to, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(to);
        DateTimeOffset now = _clock.GetUtcNow();
        var id = EnvelopeId.New(now);
        Model job = Model.CreateJob(new EnvelopeHeader(_me.Tenant, id, _me.Machine, to, now), new JobFields("probe", TimeoutMinutes: 2), Encoding.UTF8.GetBytes(Body));
        EnvelopeResult signed = await _signer.SignAsync(job, _signWith, ct).ConfigureAwait(false);
        if (!signed.IsAccepted)
        {
            return await RecordAsync(new ProbeRecord(id, to, now, null, ProbeOutcome.PushFailed, "sign: " + signed.Rejection!.Detail, null), ct).ConfigureAwait(false);
        }

        WriteOutcome submitted = await _bus.SubmitAsync(signed.Envelope!, ct).ConfigureAwait(false);
        DateTimeOffset sent = _clock.GetUtcNow();
        if (submitted is not Pushed)
        {
            string reason = submitted switch
            {
                Deferred deferred => "deferred: " + deferred.Detail,
                WriteFailed failed => failed.Stage + ": " + failed.Detail,
                _ => "not pushed",
            };
            return await RecordAsync(new ProbeRecord(id, to, sent, null, ProbeOutcome.PushFailed, reason, null), ct).ConfigureAwait(false);
        }

        LogSent(id.Value, to.Value);
        DateTimeOffset deadline = sent + timeout;
        PollState state = PollState.Initial(sent);
        while (true)
        {
            now = _clock.GetUtcNow();
            if (now >= deadline)
            {
                return await RecordAsync(new ProbeRecord(id, to, sent, null, ProbeOutcome.Timeout, null, null), ct).ConfigureAwait(false);
            }

            DateTimeOffset due = state.NextDueUtc < deadline ? state.NextDueUtc : deadline;
            if (due > now)
            {
                await Task.Delay(due - now, _clock, ct).ConfigureAwait(false);
                if (_clock.GetUtcNow() >= deadline)
                {
                    continue;
                }
            }

            HeadProbe probe = await _provider.ProbeHeadAsync(state.ETag, ct).ConfigureAwait(false);
            PollTransition transition = state.Apply(probe, PollMode.Active, _clock.GetUtcNow(), (_jitter.NextDouble() * 2) - 1, _options);
            state = transition.State;
            if (!transition.Pull)
            {
                continue;
            }

            BusSyncResult pull = await _bus.PullAsync(ct).ConfigureAwait(false);
            if (!pull.Succeeded)
            {
                continue;
            }

            IReadOnlyList<Model> reports = await _bus.FindReportsAsync(to, id, ct).ConfigureAwait(false);
            if (reports.Count == 0)
            {
                continue;
            }

            Model report = reports[0];
            DateTimeOffset observed = _clock.GetUtcNow();
            ProbeRecord record = report.Report!.Status == ReportStatus.Rejected
                ? new ProbeRecord(id, to, sent, observed, ProbeOutcome.Rejected, report.Report.Reason, null)
                : new ProbeRecord(id, to, sent, observed, ProbeOutcome.Done, null, (long)(observed - sent).TotalMilliseconds);
            return await RecordAsync(record, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Sends <paramref name="count"/> probes, <paramref name="interval"/> apart (measured from one probe's end to the next start).</summary>
    /// <param name="to">The target machine.</param>
    /// <param name="count">How many probes.</param>
    /// <param name="interval">The pause between probes.</param>
    /// <param name="timeout">The per-probe report timeout.</param>
    /// <param name="onRecord">Called after each probe with its record (the verb prints a line).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The records, in order.</returns>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="count"/> is not positive.</exception>
    public async Task<IReadOnlyList<ProbeRecord>> SendManyAsync(MachineName to, int count, TimeSpan interval, TimeSpan timeout, Action<ProbeRecord>? onRecord, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(to);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        var records = new List<ProbeRecord>(count);
        for (int i = 0; i < count; i++)
        {
            if (i > 0 && interval > TimeSpan.Zero)
            {
                await Task.Delay(interval, _clock, ct).ConfigureAwait(false);
            }

            ProbeRecord record = await SendOneAsync(to, timeout, ct).ConfigureAwait(false);
            records.Add(record);
            onRecord?.Invoke(record);
        }

        return records;
    }

    private async Task<ProbeRecord> RecordAsync(ProbeRecord record, CancellationToken ct)
    {
        await _ledger.AppendAsync(record, ct).ConfigureAwait(false);
        string outcome = ProbeOutcomeWire.ToWire(record.Outcome);
        LogOutcome(record.Id.Value, record.To.Value, outcome, record.RoundTripMs);
        return record;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "probe {Id} sent to {To}")]
    private partial void LogSent(string id, string to);

    [LoggerMessage(Level = LogLevel.Information, Message = "probe {Id} to {To}: {Outcome} ({RoundTripMs} ms)")]
    private partial void LogOutcome(string id, string to, string outcome, long? roundTripMs);
}
