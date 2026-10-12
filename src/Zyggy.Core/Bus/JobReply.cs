using Zyggy.Core.Envelope;

namespace Zyggy.Core.Bus;

/// <summary>A handler's answer to a job, before it becomes a signed <c>report</c> envelope (founding spec §4 report fields).</summary>
/// <param name="Status">The report status.</param>
/// <param name="Reason">The <c>reason</c> token; present exactly when <paramref name="Status"/> is not <c>done</c>.</param>
/// <param name="Body">The report body (data, never instructions).</param>
/// <param name="Started">When the work started.</param>
/// <param name="Finished">When the work finished.</param>
public sealed record JobReply(ReportStatus Status, string? Reason, ReadOnlyMemory<byte> Body, DateTimeOffset Started, DateTimeOffset Finished);
