using System.Text.Json;

using Zyggy.Core.Runs;

namespace Zyggy.Core.Models;

/// <summary>The outcome of one model session.</summary>
/// <param name="Outcome">Succeeded or failed.</param>
/// <param name="Reason">The closed §9 reason when failed.</param>
/// <param name="FailureDetail">A token from <see cref="ModelFailureDetail"/> when failed.</param>
/// <param name="ResultText">The result event's <c>result</c> text.</param>
/// <param name="StructuredOutput">The schema-validated output when a schema was given.</param>
/// <param name="CostUsd">The result's <c>total_cost_usd</c> (a client-side estimate).</param>
/// <param name="NumTurns">The result's <c>num_turns</c>.</param>
/// <param name="Duration">Wall-clock duration of the process.</param>
/// <param name="Model">The model named by the session's init event.</param>
/// <param name="InputTokens">Input tokens from the result's usage.</param>
/// <param name="OutputTokens">Output tokens from the result's usage.</param>
/// <param name="ExitCode">The process exit code, when it exited.</param>
/// <param name="PermissionDenials">How many tool calls were denied.</param>
public sealed record ModelRunResult(
    ModelRunOutcome Outcome,
    RunFailureReason? Reason,
    string? FailureDetail,
    string? ResultText,
    JsonElement? StructuredOutput,
    decimal? CostUsd,
    int? NumTurns,
    TimeSpan Duration,
    string? Model,
    long? InputTokens,
    long? OutputTokens,
    int? ExitCode,
    int PermissionDenials)
{
    /// <summary>Gets the names of the denied tools (<c>permission_denials[].tool_name</c>), in the order reported.</summary>
    public IReadOnlyList<string> PermissionDenialTools { get; init; } = [];
}
