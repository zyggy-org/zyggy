using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zyggy.Core.Health;

/// <summary>The source-generated serializer of <see cref="NodeHealth"/> (§9: no reflection-based JSON): camelCase, nulls written.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(NodeHealth))]
internal sealed partial class NodeHealthJsonContext : JsonSerializerContext;

/// <summary>The two JSON calls over the §11 body: the server writes it, <c>zyggy status</c> reads it (deliverable 08).</summary>
internal static class NodeHealthJson
{
    /// <summary>The body as the endpoint writes it.</summary>
    internal static string Serialize(NodeHealth health) => JsonSerializer.Serialize(health, NodeHealthJsonContext.Default.NodeHealth);

    /// <summary>The body read back; <see langword="null"/> for JSON <c>null</c>.</summary>
    /// <exception cref="JsonException">The bytes are not a health body.</exception>
    internal static NodeHealth? Deserialize(ReadOnlySpan<byte> utf8) => JsonSerializer.Deserialize(utf8, NodeHealthJsonContext.Default.NodeHealth);
}

/// <summary>
/// Writes an instant as <c>YYYY-MM-DDTHH:MM:SSZ</c> (§11): converted to UTC, whole seconds (plan 07 A10 — the default form carries
/// <c>+00:00</c> and fractional seconds). Reads the same form or any ISO 8601 instant.
/// </summary>
internal sealed class UtcSecondsConverter : JsonConverter<DateTimeOffset>
{
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DateTimeOffset.Parse(reader.GetString() ?? string.Empty, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture));
}
