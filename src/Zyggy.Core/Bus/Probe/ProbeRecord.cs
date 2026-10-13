using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Bus.Probe;

/// <summary>One line of the sender's probe ledger (deliverable 04): what was sent, to whom, and how it ended, all on the sender's clock (O21).</summary>
/// <param name="Id">The probe job's id.</param>
/// <param name="To">The target machine.</param>
/// <param name="SentUtc">When the job was pushed (or the push gave up).</param>
/// <param name="ObservedUtc">When the report was observed, when one was.</param>
/// <param name="Outcome">How the probe ended.</param>
/// <param name="Reason">The report's <c>reason</c> for a rejected probe, or the push detail.</param>
/// <param name="RoundTripMs">The sender-side round trip in milliseconds for a <c>done</c> probe.</param>
public sealed record ProbeRecord(
    EnvelopeId Id,
    MachineName To,
    DateTimeOffset SentUtc,
    DateTimeOffset? ObservedUtc,
    ProbeOutcome Outcome,
    string? Reason,
    long? RoundTripMs);
