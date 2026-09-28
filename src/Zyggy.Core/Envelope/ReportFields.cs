namespace Zyggy.Core.Envelope;

/// <summary>The fields of a <c>report</c> envelope (founding spec §4 "Mandatory fields per type", row <c>report</c>).</summary>
/// <param name="Status">The <c>status</c> (mandatory).</param>
/// <param name="Reason">The <c>reason</c> snake_case token; present exactly when <paramref name="Status"/> is not <c>done</c>.</param>
/// <param name="Started">The optional <c>started</c> instant, in UTC.</param>
/// <param name="Finished">The optional <c>finished</c> instant, in UTC.</param>
/// <param name="DurationSeconds">The optional <c>duration_seconds</c>.</param>
/// <param name="CostUsd">The optional <c>cost_usd</c>.</param>
/// <param name="InputTokens">The optional <c>input_tokens</c>.</param>
/// <param name="OutputTokens">The optional <c>output_tokens</c>.</param>
/// <param name="NumTurns">The optional <c>num_turns</c> (§11 cost tracking).</param>
/// <param name="Model">The optional <c>model</c>.</param>
/// <param name="FilesChanged">The <c>files_changed</c> list; <see langword="null"/> when absent.</param>
/// <param name="DiffRef">The optional <c>diff_ref</c>.</param>
public sealed record ReportFields(
    ReportStatus Status,
    string? Reason = null,
    DateTimeOffset? Started = null,
    DateTimeOffset? Finished = null,
    int? DurationSeconds = null,
    decimal? CostUsd = null,
    int? InputTokens = null,
    int? OutputTokens = null,
    int? NumTurns = null,
    string? Model = null,
    IReadOnlyList<string>? FilesChanged = null,
    string? DiffRef = null);
