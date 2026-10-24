using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Envelope;

/// <summary>The header scalars of a file read leniently by <see cref="EnvelopeParser.PeekHeader"/>: each typed when parsable, <see langword="null"/> otherwise.</summary>
/// <param name="Type">The <c>type</c>.</param>
/// <param name="From">The <c>from</c>.</param>
/// <param name="To">The <c>to</c>.</param>
/// <param name="Created">The <c>created</c>.</param>
/// <param name="InReplyTo">The <c>in_reply_to</c>.</param>
/// <param name="Status">A report's <c>status</c>.</param>
/// <param name="Reason">A report's <c>reason</c>.</param>
/// <param name="CostUsd">A report's <c>cost_usd</c>.</param>
internal sealed record PeekedHeader(
    EnvelopeType? Type,
    MachineName? From,
    MachineName? To,
    DateTimeOffset? Created,
    EnvelopeId? InReplyTo,
    ReportStatus? Status,
    string? Reason,
    decimal? CostUsd);
