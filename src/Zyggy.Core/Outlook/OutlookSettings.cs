using System.Text.Json;

namespace Zyggy.Core.Outlook;

/// <summary>The <c>outlook</c> section of the work laptop's <c>node.json</c> that the read path uses (spec 58 Configuration).</summary>
/// <param name="Enabled"><c>outlook.enabled</c> (default false).</param>
/// <param name="PolicyMirror"><c>outlook.policyConfirmed</c>: the mirror of the instance record, used only without a <c>work-boundary.json</c> copy.</param>
/// <param name="ReadBodyMaxChars"><c>outlook.read.bodyMaxChars</c>: where a read body is cut.</param>
/// <param name="ReadMaxItems"><c>outlook.read.maxItems</c>: the most items one list call returns.</param>
internal sealed record OutlookSettings(bool Enabled, OutlookPolicyRecord? PolicyMirror, int ReadBodyMaxChars, int ReadMaxItems)
{
    /// <summary>The default of <c>outlook.read.bodyMaxChars</c>.</summary>
    public const int DefaultReadBodyMaxChars = 8000;

    /// <summary>The default of <c>outlook.read.maxItems</c>.</summary>
    public const int DefaultReadMaxItems = 50;

    /// <summary>Gets the settings of a node without an <c>outlook</c> section: disabled, no mirror, the defaults.</summary>
    public static OutlookSettings Disabled { get; } = new(false, null, DefaultReadBodyMaxChars, DefaultReadMaxItems);

    /// <summary>Reads the <c>outlook</c> object of <c>node.json</c>; a missing or malformed value falls back to its default (disabled, no mirror).</summary>
    /// <param name="outlook">The object, or <see langword="null"/> when the key is absent.</param>
    /// <returns>The settings.</returns>
    public static OutlookSettings FromNodeJson(JsonElement? outlook)
    {
        if (outlook is not { ValueKind: JsonValueKind.Object } section)
        {
            return Disabled;
        }

        var enabled = section.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.True;
        var mirror = section.TryGetProperty("policyConfirmed", out var p) ? OutlookPolicyRecord.Parse(p) : null;
        var read = section.TryGetProperty("read", out var r) && r.ValueKind == JsonValueKind.Object ? r : (JsonElement?)null;
        return new OutlookSettings(
            enabled,
            mirror,
            PositiveInt(read, "bodyMaxChars") ?? DefaultReadBodyMaxChars,
            PositiveInt(read, "maxItems") ?? DefaultReadMaxItems);
    }

    private static int? PositiveInt(JsonElement? section, string key) =>
        section is { } s && s.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number > 0
            ? number
            : null;
}
