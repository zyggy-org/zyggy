using System.Text.Json;

namespace Zyggy.Core.Hub;

/// <summary>
/// The Hub's own argument check, shared by both tools and run before anything else (spec 11): a <c>tenant</c> or <c>user</c> property
/// first, then any undeclared property, wrong JSON kind or missing required property. The SDK's schema handling is never trusted for
/// policy.
/// </summary>
internal static class ToolArguments
{
    public const string NamedPrincipal = "refused: tenant and user are never request fields";

    public const string Invalid = "refused: invalid arguments";

    /// <summary>The one stderr line written for each call that named a principal.</summary>
    public const string NamedPrincipalDiagnostic = "hub: refused: request named a principal\n";

    /// <summary>The arguments as one JSON object, exactly as received; absent arguments are an empty object.</summary>
    public static JsonElement AsObject(IDictionary<string, JsonElement>? arguments)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in arguments ?? new Dictionary<string, JsonElement>())
            {
                writer.WritePropertyName(name);
                value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

    /// <summary>Returns <see langword="null"/> when the arguments hold only declared properties of their kinds, else the refusal text.</summary>
    public static string? Refuse(
        JsonElement arguments,
        IReadOnlyDictionary<string, JsonValueKind> declared,
        IReadOnlyCollection<string> required,
        out bool namedPrincipal)
    {
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(required);
        namedPrincipal = false;
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return Invalid;
        }

        if (arguments.EnumerateObject().Any(property => property.NameEquals("tenant") || property.NameEquals("user")))
        {
            namedPrincipal = true;
            return NamedPrincipal;
        }

        var wellFormed = arguments.EnumerateObject().All(property => declared.TryGetValue(property.Name, out var kind) && property.Value.ValueKind == kind)
            && required.All(name => arguments.TryGetProperty(name, out _));
        return wellFormed ? null : Invalid;
    }
}
