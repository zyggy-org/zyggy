using System.Text.Json.Nodes;

namespace Zyggy.Core.Tests.Infrastructure;

/// <summary>Builds Claude Code <c>stream-json</c> lines (system/init, assistant, result) for the runner tests.</summary>
internal static class StreamLines
{
    public static string Init(string model = "fake-model-1") =>
        new JsonObject { ["type"] = "system", ["subtype"] = "init", ["model"] = model, ["session_id"] = "s-1" }.ToJsonString();

    public static string Assistant(string text = "working") =>
        new JsonObject
        {
            ["type"] = "assistant",
            ["message"] = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }) },
        }.ToJsonString();

    public static string Result(
        bool isError = false,
        string subtype = "success",
        string? resultText = "done",
        string? structuredOutputJson = null,
        int permissionDenials = 0,
        bool withTotalCost = true,
        bool withLegacyCost = false)
    {
        var result = new JsonObject
        {
            ["type"] = "result",
            ["subtype"] = subtype,
            ["is_error"] = isError,
            ["num_turns"] = 2,
            ["duration_ms"] = 1500,
            ["session_id"] = "s-1",
            ["result"] = resultText,
            ["usage"] = new JsonObject { ["input_tokens"] = 100, ["output_tokens"] = 20 },
            ["permission_denials"] = new JsonArray(Enumerable.Range(0, permissionDenials)
                .Select(i => (JsonNode?)new JsonObject { ["tool_name"] = $"Tool{i}" }).ToArray()),
        };
        if (withTotalCost)
        {
            result["total_cost_usd"] = 0.0123m;
        }

        if (withLegacyCost)
        {
            result["cost_usd"] = 0.5m;
        }

        if (structuredOutputJson is not null)
        {
            result["structured_output"] = JsonNode.Parse(structuredOutputJson);
        }

        return result.ToJsonString();
    }
}
