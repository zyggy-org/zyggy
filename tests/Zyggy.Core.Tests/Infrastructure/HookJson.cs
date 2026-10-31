using System.Text.Json;

namespace Zyggy.Core.Tests.Infrastructure;

/// <summary>The template's <c>stop-input.json</c> fixture (byte for byte) and builders for the hook JSON a laptop's Claude Code sends (spec 12).</summary>
public static class HookJson
{
    public const string SessionId = "0b7c3d1e-4f5a-4b6c-8d7e-9f0a1b2c3d4e";

    public const string FixtureFirstLine = "Done: the plan for deliverable 27 is written and reviewed.";

    public const string Fixture = """
        {
          "session_id": "0b7c3d1e-4f5a-4b6c-8d7e-9f0a1b2c3d4e",
          "transcript_path": "/tmp/project/0b7c3d1e-4f5a-4b6c-8d7e-9f0a1b2c3d4e.jsonl",
          "cwd": "/tmp/project",
          "permission_mode": "auto",
          "hook_event_name": "Stop",
          "stop_hook_active": false,
          "last_assistant_message": "Done:  the plan for\tdeliverable 27 is  written and reviewed.\nThe runbook has nine steps.\nNext: the owner creates the repositories.",
          "stop_reason": "end_turn"
        }

        """;

    /// <summary>A <c>Stop</c> hook input; <paramref name="message"/> <see langword="null"/> omits the member.</summary>
    public static string Stop(string? message, bool active = false, string cwd = "/tmp/project", string? sessionId = SessionId)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["session_id"] = sessionId,
            ["transcript_path"] = "/tmp/project/transcript.jsonl",
            ["cwd"] = cwd,
            ["permission_mode"] = "auto",
            ["hook_event_name"] = "Stop",
            ["stop_hook_active"] = active,
            ["stop_reason"] = "end_turn",
        };
        if (message is not null)
        {
            fields["last_assistant_message"] = message;
        }

        return JsonSerializer.Serialize(fields);
    }

    /// <summary>A <c>SessionEnd</c> hook input.</summary>
    public static string SessionEnd(string? sessionId = SessionId, string reason = "exit") =>
        JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["session_id"] = sessionId,
            ["transcript_path"] = "/tmp/project/transcript.jsonl",
            ["cwd"] = "/tmp/project",
            ["hook_event_name"] = "SessionEnd",
            ["reason"] = reason,
        });
}
