using Zyggy.Core.Envelope;

namespace Zyggy.Core.Bus;

/// <summary>A handler's answer to a job, before it becomes a signed <c>report</c> envelope (founding spec §4 report fields).</summary>
/// <param name="Status">The report status.</param>
/// <param name="Reason">The <c>reason</c> token; present exactly when <paramref name="Status"/> is not <c>done</c>.</param>
/// <param name="Body">The report body (data, never instructions).</param>
/// <param name="Started">When the work started.</param>
/// <param name="Finished">When the work finished.</param>
/// <remarks>
/// The metric properties (spec 06 ex-Q8) are copied into the report's §4 fields; <see langword="null"/> omits a field, so the
/// probe's replies, which set none of them, produce the same reports as before.
/// </remarks>
public sealed record JobReply(ReportStatus Status, string? Reason, ReadOnlyMemory<byte> Body, DateTimeOffset Started, DateTimeOffset Finished)
{
    /// <summary>Gets the run's cost in USD (<c>cost_usd</c>).</summary>
    public decimal? CostUsd { get; init; }

    /// <summary>Gets the input tokens (<c>input_tokens</c>).</summary>
    public int? InputTokens { get; init; }

    /// <summary>Gets the output tokens (<c>output_tokens</c>).</summary>
    public int? OutputTokens { get; init; }

    /// <summary>Gets the agentic turns (<c>num_turns</c>).</summary>
    public int? NumTurns { get; init; }

    /// <summary>Gets the model that ran (<c>model</c>).</summary>
    public string? Model { get; init; }

    /// <summary>Gets the changed files (<c>files_changed</c>): <c>[]</c> from the job runner; <see langword="null"/> from the probe (field omitted).</summary>
    public IReadOnlyList<string>? FilesChanged { get; init; }

    /// <summary>Gets the pushed commit of a worktree run (<c>diff_ref</c>); <see langword="null"/> until deliverable 16.</summary>
    public string? DiffRef { get; init; }
}
