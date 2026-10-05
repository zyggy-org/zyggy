using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Zyggy.Core.M365.Guard;

/// <summary>The guard's one output: the PreToolUse deny line, byte for byte as <c>jq -nc</c> printed it.</summary>
internal static class GuardOutput
{
    private static readonly JsonWriterOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Deny(string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, Compact))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("hookSpecificOutput");
            writer.WriteString("hookEventName", "PreToolUse");
            writer.WriteString("permissionDecision", "deny");
            writer.WriteString("permissionDecisionReason", "m365-guard: refused: " + reason);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }
}
