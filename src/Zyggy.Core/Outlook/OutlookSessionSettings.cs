using System.Text.Json;
using System.Text.RegularExpressions;

namespace Zyggy.Core.Outlook;

/// <summary>One Claude Code settings file the session reads.</summary>
/// <param name="Source">The file, as the refusal names it.</param>
/// <param name="Text">Its text, or <see langword="null"/> when there is no such file.</param>
/// <param name="Unreadable">Whether the file exists but could not be read.</param>
internal sealed record SettingsLayer(string Source, string? Text, bool Unreadable = false);

/// <summary>
/// Spec 58 Q24 (accepted by the owner 2026-11-22, widened by Central to every tool): in the session that may send or move work mail,
/// every tool use beyond Read and the <c>zyggy outlook</c> read verbs needs the owner's answer to a prompt. The guard checks that before
/// it passes a send or move to the prompt: the session's permission mode is <c>default</c>, and no settings file auto-allows anything
/// else that a deny rule does not take back. It refuses on a file it cannot read (fail closed).
/// </summary>
/// <remarks>
/// Rules that stay allowed: <c>Read</c>, <c>Grep</c>, <c>Glob</c> (bare or with a path: Claude Code never prompts for them anyway), and
/// <c>Bash(zyggy outlook &lt;read verb&gt;)</c>, with <c>zyggy.exe</c>, a trailing <c> *</c> or <c>:*</c>, no shell metacharacter. An allow
/// rule is taken back by a deny rule written the same or by a bare deny of its tool (deny wins in Claude Code). What the guard cannot
/// see — a <c>--allowedTools</c> flag, an older client without <c>permission_mode</c> (refused), another hook that allows — is the
/// residual of the settings note (<c>_plans/zyggy-core-notes/58-s7-work-mail-settings.md</c>).
/// </remarks>
internal static partial class OutlookSessionSettings
{
    private static readonly string[] FileReads = ["Read", "Grep", "Glob"];

    /// <summary>Why the session may not send or move, or <see langword="null"/>.</summary>
    /// <param name="permissionMode">The hook input's <c>permission_mode</c>.</param>
    /// <param name="layers">Every settings file the session reads (user, project, local project, managed).</param>
    /// <returns>The problem, or <see langword="null"/>.</returns>
    public static string? Problem(string? permissionMode, IReadOnlyList<SettingsLayer> layers)
    {
        ArgumentNullException.ThrowIfNull(layers);
        if (permissionMode != "default")
        {
            return "the session's permission mode is " + (permissionMode ?? "unknown") + " (send and move need the default mode, every other tool on a prompt)";
        }

        var allows = new List<(string Source, string Rule)>();
        var denies = new HashSet<string>(StringComparer.Ordinal);
        foreach (SettingsLayer layer in layers)
        {
            if (layer.Unreadable)
            {
                return "cannot read the settings file " + layer.Source;
            }

            if (layer.Text is null)
            {
                continue;
            }

            if (!TryRules(layer.Text, out List<string> allow, out List<string> deny))
            {
                return "cannot read the settings file " + layer.Source;
            }

            allows.AddRange(allow.Select(rule => (layer.Source, rule)));
            denies.UnionWith(deny.Select(rule => rule.Trim()));
        }

        foreach (var (source, rule) in allows)
        {
            string trimmed = rule.Trim();
            if (!Permitted(trimmed) && !denies.Contains(trimmed) && !denies.Contains(Tool(trimmed)))
            {
                return source + " auto-allows " + trimmed + " (only Read and the zyggy outlook read verbs may run without the owner's prompt)";
            }
        }

        return null;
    }

    private static bool TryRules(string text, out List<string> allow, out List<string> deny)
    {
        allow = [];
        deny = [];
        try
        {
            using JsonDocument document = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!document.RootElement.TryGetProperty("permissions", out JsonElement permissions))
            {
                return true;
            }

            return permissions.ValueKind == JsonValueKind.Object && Strings(permissions, "allow", allow) && Strings(permissions, "deny", deny);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool Strings(JsonElement permissions, string name, List<string> into)
    {
        if (!permissions.TryGetProperty(name, out JsonElement list))
        {
            return true;
        }

        if (list.ValueKind != JsonValueKind.Array || list.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String))
        {
            return false;
        }

        into.AddRange(list.EnumerateArray().Select(e => e.GetString()!));
        return true;
    }

    private static string Tool(string rule)
    {
        int open = rule.IndexOf('(', StringComparison.Ordinal);
        return (open < 0 ? rule : rule[..open]).Trim();
    }

    private static bool Permitted(string rule) =>
        (FileReads.Contains(Tool(rule), StringComparer.Ordinal) && !rule.Contains(',', StringComparison.Ordinal))
        || (ReadVerbRule().Match(rule) is { Success: true } m && WorkMailToolMix.ReadVerbs.Contains(m.Groups["verb"].Value, StringComparer.Ordinal));

    [GeneratedRegex(@"\ABash\(zyggy(?:\.exe)? outlook (?<verb>[a-z-]+)(?: \*|:\*)?\)\z", RegexOptions.CultureInvariant)]
    private static partial Regex ReadVerbRule();
}
