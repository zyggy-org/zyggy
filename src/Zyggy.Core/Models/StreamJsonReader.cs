using System.Text;
using System.Text.Json;

namespace Zyggy.Core.Models;

/// <summary>
/// Reads a Claude Code <c>stream-json</c> output line by line: the model from the <c>system/init</c> event and every
/// field of the final <c>result</c> event, and the text of the last <c>assistant</c> message (at most
/// <see cref="MaxAssistantTextLength"/> characters). Lines that are not JSON objects are ignored; a result line that cannot be
/// read is marked, never thrown. Output beyond the byte cap marks <see cref="OutputTooLarge"/>, yet every line is still parsed,
/// so a late <c>result</c> is read (spec 06 ex-Q4); memory held is bounded by the last text, not by the stream.
/// </summary>
internal sealed class StreamJsonReader(int maxBytes)
{
    public const int MaxAssistantTextLength = 2000;

    private long _bytes;

    public bool OutputTooLarge { get; private set; }

    public bool SawResult { get; private set; }

    public bool ResultUnparseable { get; private set; }

    public string? Model { get; private set; }

    public bool IsError { get; private set; }

    public string? Subtype { get; private set; }

    public string? ResultText { get; private set; }

    public JsonElement? StructuredOutput { get; private set; }

    public decimal? CostUsd { get; private set; }

    public int? NumTurns { get; private set; }

    public long? DurationMs { get; private set; }

    public long? InputTokens { get; private set; }

    public long? OutputTokens { get; private set; }

    public int PermissionDenials { get; private set; }

    public IReadOnlyList<string> PermissionDenialTools { get; private set; } = [];

    /// <summary>Gets the concatenated <c>text</c> blocks of the last assistant message that had any, at most 2,000 characters.</summary>
    public string? LastAssistantText { get; private set; }

    public void Accept(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        _bytes += Encoding.UTF8.GetByteCount(line) + 1;
        if (_bytes > maxBytes)
        {
            OutputTooLarge = true;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            if (LooksLikeResult(line))
            {
                SawResult = true;
                ResultUnparseable = true;
            }

            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
            {
                return;
            }

            switch (type.GetString())
            {
                case "system" when root.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String:
                    Model = model.GetString();
                    break;
                case "assistant":
                    ReadAssistant(root);
                    break;
                case "result":
                    SawResult = true;
                    ResultUnparseable = !TryReadResult(root);
                    break;
            }
        }
    }

    private void ReadAssistant(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var texts = content.EnumerateArray()
            .Where(b => b.ValueKind == JsonValueKind.Object && String(b, "type") == "text" && b.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
            .Select(b => b.GetProperty("text").GetString()!)
            .ToList();
        if (texts.Count == 0)
        {
            return;
        }

        var text = string.Join('\n', texts);
        LastAssistantText = text.Length > MaxAssistantTextLength ? text[..MaxAssistantTextLength] : text;
    }

    private static bool LooksLikeResult(string line) =>
        line.Replace(" ", "", StringComparison.Ordinal).Contains("\"type\":\"result\"", StringComparison.Ordinal);

    private bool TryReadResult(JsonElement root)
    {
        try
        {
            IsError = root.TryGetProperty("is_error", out var isError) && isError.GetBoolean();
            Subtype = String(root, "subtype");
            ResultText = String(root, "result");
            CostUsd = root.TryGetProperty("total_cost_usd", out var cost) && cost.ValueKind != JsonValueKind.Null ? cost.GetDecimal() : null;
            NumTurns = root.TryGetProperty("num_turns", out var turns) && turns.ValueKind != JsonValueKind.Null ? turns.GetInt32() : null;
            DurationMs = root.TryGetProperty("duration_ms", out var ms) && ms.ValueKind != JsonValueKind.Null ? ms.GetInt64() : null;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                InputTokens = usage.TryGetProperty("input_tokens", out var input) ? input.GetInt64() : null;
                OutputTokens = usage.TryGetProperty("output_tokens", out var output) ? output.GetInt64() : null;
            }

            PermissionDenials = root.TryGetProperty("permission_denials", out var denials) && denials.ValueKind == JsonValueKind.Array
                ? denials.GetArrayLength()
                : 0;
            PermissionDenialTools = denials.ValueKind == JsonValueKind.Array
                ? [.. denials.EnumerateArray().Where(d => d.ValueKind == JsonValueKind.Object && d.TryGetProperty("tool_name", out var n) && n.ValueKind == JsonValueKind.String).Select(d => d.GetProperty("tool_name").GetString()!)]
                : [];
            StructuredOutput = root.TryGetProperty("structured_output", out var structured) && structured.ValueKind != JsonValueKind.Null
                ? structured.Clone()
                : null;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    private static string? String(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;
}
