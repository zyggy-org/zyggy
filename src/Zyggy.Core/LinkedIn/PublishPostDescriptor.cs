using System.Text.Json;

namespace Zyggy.Core.LinkedIn;

/// <summary>
/// The one tool's protocol face (spec 36 Contracts "MCP tool", AC-1): its name, its description and its strict input schema, the text's
/// <c>maxLength</c> following <c>post.max_chars</c>. The description guides the model; the permission prompt is the control.
/// </summary>
internal static class PublishPostDescriptor
{
    public const string Name = PublishPostTool.ToolName;

    public const string Description =
        "Publish one text post on the owner's personal LinkedIn profile. Call only after the owner approved this exact text in his latest message. "
        + "The text is published exactly as given.";

    /// <summary><c>{"type":"object","properties":{text,visibility},"required":[…],"additionalProperties":false}</c>.</summary>
    public static JsonElement InputSchema(int maxChars)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "object");
            writer.WriteStartObject("properties");
            writer.WriteStartObject("text");
            writer.WriteString("type", "string");
            writer.WriteNumber("minLength", 1);
            writer.WriteNumber("maxLength", maxChars);
            writer.WriteEndObject();
            writer.WriteStartObject("visibility");
            writer.WriteString("type", "string");
            writer.WriteStartArray("enum");
            writer.WriteStringValue(PostVisibility.Public.Wire());
            writer.WriteStringValue(PostVisibility.Connections.Wire());
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteStartArray("required");
            writer.WriteStringValue("text");
            writer.WriteStringValue("visibility");
            writer.WriteEndArray();
            writer.WriteBoolean("additionalProperties", false);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }
}
