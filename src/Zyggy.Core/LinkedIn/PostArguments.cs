using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Zyggy.Core.LinkedIn;

/// <summary>
/// The arguments of <c>publish_post</c> (spec 36 AC-1): an object with exactly <c>text</c> (a string) and <c>visibility</c>
/// (<c>PUBLIC</c> | <c>CONNECTIONS</c>). The text is kept byte for byte — no trim, no normalisation; its length and content are
/// <see cref="PostPolicy"/>'s to judge.
/// </summary>
internal sealed record PostArguments(string Text, PostVisibility Visibility)
{
    public const string Invalid = "invalid arguments";

    /// <summary>Gets the text's length in Unicode scalar values (an emoji is one; spec 36 Assumption 1).</summary>
    public int Characters => Text.EnumerateRunes().Count();

    public static bool TryParse(JsonElement arguments, [NotNullWhen(true)] out PostArguments? parsed, [NotNullWhen(false)] out string? reason)
    {
        parsed = null;
        reason = Invalid;
        if (arguments.ValueKind != JsonValueKind.Object
            || arguments.EnumerateObject().Count() != 2
            || !arguments.TryGetProperty("text", out var text)
            || text.ValueKind != JsonValueKind.String
            || !arguments.TryGetProperty("visibility", out var visibility)
            || visibility.ValueKind != JsonValueKind.String
            || !PostVisibilityWire.TryParse(visibility.GetString(), out var parsedVisibility))
        {
            return false;
        }

        parsed = new PostArguments(text.GetString()!, parsedVisibility);
        reason = null;
        return true;
    }
}
