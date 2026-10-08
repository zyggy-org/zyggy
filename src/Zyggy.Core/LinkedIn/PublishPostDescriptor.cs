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
        "Publish one text post on the owner's personal LinkedIn profile, optionally with one image the owner was shown (image_path inside "
        + "the media folder and its image_sha256). Call only after the owner approved this exact text, and the image, in his latest message. "
        + "The text is published exactly as given.";

    /// <summary><c>{"type":"object","properties":{text,visibility,image_path,image_sha256,image_alt},"required":[text,visibility],"additionalProperties":false}</c>.</summary>
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
            writer.WriteStartObject("image_path");
            writer.WriteString("type", "string");
            writer.WriteNumber("minLength", 1);
            writer.WriteEndObject();
            writer.WriteStartObject("image_sha256");
            writer.WriteString("type", "string");
            writer.WriteString("pattern", "^[0-9a-f]{64}$");
            writer.WriteEndObject();
            writer.WriteStartObject("image_alt");
            writer.WriteString("type", "string");
            writer.WriteNumber("maxLength", PostArguments.MaxAltChars);
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
