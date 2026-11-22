using System.Text.Json;

namespace Zyggy.Core.Outlook;

/// <summary>
/// The action keys of the <c>outlook</c> section of the work laptop's <c>node.json</c> (spec 58 Configuration): <c>outlook.actions</c>,
/// <c>outlook.send.maxRecipients</c>, <c>outlook.send.bodyMaxChars</c>, <c>outlook.move.excludedFolders</c>.
/// </summary>
/// <param name="Actions">The enabled actions: a subset of <c>send</c>, <c>move</c> (default none: read only).</param>
/// <param name="SendMaxRecipients">The most To and Cc recipients of one send.</param>
/// <param name="SendBodyMaxChars">The longest body of one send, in characters, the quoted history included.</param>
/// <param name="MoveExcludedFolders">Folders of the default store a move may not target (nor their subfolders).</param>
internal sealed record OutlookActionSettings(
    IReadOnlySet<string> Actions,
    int SendMaxRecipients,
    int SendBodyMaxChars,
    IReadOnlyList<string> MoveExcludedFolders)
{
    /// <summary>The m365 send default for recipients (<c>actions.send.max_recipients</c> of the template's <c>m365.json</c>); tighten only.</summary>
    public const int DefaultSendMaxRecipients = 10;

    /// <summary>The m365 send default for the body (<c>actions.send.body_max_chars</c> of the template's <c>m365.json</c>); tighten only.</summary>
    public const int DefaultSendBodyMaxChars = 4000;

    /// <summary>Gets the settings without an <c>outlook</c> section: no action, the defaults.</summary>
    public static OutlookActionSettings ReadOnly { get; } = new(
        new HashSet<string>(StringComparer.Ordinal), DefaultSendMaxRecipients, DefaultSendBodyMaxChars, []);

    /// <summary>Reads the action keys of the <c>outlook</c> object of <c>node.json</c>; a missing or malformed value is its default.</summary>
    /// <param name="outlook">The object, or <see langword="null"/> when the key is absent.</param>
    /// <returns>The settings.</returns>
    public static OutlookActionSettings FromNodeJson(JsonElement? outlook)
    {
        if (outlook is not { ValueKind: JsonValueKind.Object } section)
        {
            return ReadOnly;
        }

        // Only the two actions exist; any other word (delete, forward, a different case) enables nothing.
        var actions = section.TryGetProperty("actions", out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray()
                .Where(v => v.ValueKind == JsonValueKind.String && v.GetString() is "send" or "move")
                .Select(v => v.GetString()!)
                .ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        var send = Section(section, "send");
        var move = Section(section, "move");
        var excluded = move is { } m && m.TryGetProperty("excludedFolders", out var e) && e.ValueKind == JsonValueKind.Array
            ? e.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String && v.GetString()!.Trim().Length > 0).Select(v => v.GetString()!.Trim()).ToList()
            : [];

        return new OutlookActionSettings(
            actions,
            Tightened(send, "maxRecipients", DefaultSendMaxRecipients),
            Tightened(send, "bodyMaxChars", DefaultSendBodyMaxChars),
            excluded);
    }

    private static JsonElement? Section(JsonElement outlook, string key) =>
        outlook.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Object ? value : null;

    // Tighten only: a positive value below the m365 default lowers it; anything else keeps the default.
    private static int Tightened(JsonElement? section, string key, int fallback) =>
        section is { } s && s.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number > 0
            ? Math.Min(number, fallback)
            : fallback;
}
