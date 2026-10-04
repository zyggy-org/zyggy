using System.Text.Json;

namespace Zyggy.Core.Dream;

/// <summary>
/// The adaptive batch size kept across runs in <c>&lt;state dir&gt;/dream-batch.json</c> (AC-12): halved after a batch-attributable
/// failure down to <see cref="DreamOptions.BatchMinLines"/>, restored after a success; repeated failures at the minimum with the
/// same first line lead to quarantine.
/// </summary>
internal sealed record BatchSizeState(int CurrentLines, int ConsecutiveFailures, string? FirstLineHash)
{
    public static BatchSizeState Initial(DreamOptions options) => new(options.BatchMaxLines, 0, null);

    public static BatchSizeState Parse(string? json, DreamOptions options)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Initial(options);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var current = root.GetProperty("currentLines").GetInt32();
            return new BatchSizeState(
                Math.Clamp(current, options.BatchMinLines, options.BatchMaxLines),
                root.GetProperty("consecutiveFailures").GetInt32(),
                root.TryGetProperty("firstLineHash", out var head) && head.ValueKind == JsonValueKind.String ? head.GetString() : null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return Initial(options);
        }
    }

    public string Serialize() =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["currentLines"] = CurrentLines,
            ["consecutiveFailures"] = ConsecutiveFailures,
            ["firstLineHash"] = FirstLineHash,
        }) + "\n";

    public BatchSizeState OnSucceeded(DreamOptions options) => this with { CurrentLines = options.BatchMaxLines, ConsecutiveFailures = 0, FirstLineHash = null };

    public BatchSizeState OnAttributableFailure(DreamOptions options, string firstLineHash)
    {
        var atMinimum = CurrentLines <= options.BatchMinLines;
        var failures = atMinimum ? (firstLineHash == FirstLineHash ? ConsecutiveFailures + 1 : 1) : 0;
        return new BatchSizeState(Math.Max(options.BatchMinLines, CurrentLines / 2), failures, firstLineHash);
    }

    public bool ShouldQuarantine(DreamOptions options) => ConsecutiveFailures >= options.QuarantineAfter;
}
