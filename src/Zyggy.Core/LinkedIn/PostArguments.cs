using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Zyggy.Core.LinkedIn;

/// <summary>The image a post carries (plan 36b D1): the file the owner was shown, its SHA-256 and an optional alt text.</summary>
internal sealed record PostImageReference(string Path, string Sha256, string? Alt);

/// <summary>
/// The arguments of <c>publish_post</c> (spec 36 AC-1, plan 36b D1): <c>text</c> (a string) and <c>visibility</c> (<c>PUBLIC</c> |
/// <c>CONNECTIONS</c>), optionally <c>image_path</c> with <c>image_sha256</c> (64 lowercase hex) and then <c>image_alt</c> (≤ 300
/// characters, no control character); nothing else. The text is kept byte for byte — no trim, no normalisation; its length and content
/// are <see cref="PostPolicy"/>'s to judge.
/// </summary>
internal sealed partial record PostArguments(string Text, PostVisibility Visibility, PostImageReference? Image = null)
{
    public const string Invalid = "invalid arguments";
    public const int MaxAltChars = 300;

    private static readonly HashSet<string> Keys = new(["text", "visibility", "image_path", "image_sha256", "image_alt"], StringComparer.Ordinal);

    /// <summary>Gets the text's length in Unicode scalar values (an emoji is one; spec 36 Assumption 1).</summary>
    public int Characters => Text.EnumerateRunes().Count();

    public static bool TryParse(JsonElement arguments, [NotNullWhen(true)] out PostArguments? parsed, [NotNullWhen(false)] out string? reason)
    {
        parsed = null;
        reason = Invalid;
        if (arguments.ValueKind != JsonValueKind.Object
            || arguments.EnumerateObject().Any(p => !Keys.Contains(p.Name))
            || !arguments.TryGetProperty("text", out var text)
            || text.ValueKind != JsonValueKind.String
            || !arguments.TryGetProperty("visibility", out var visibility)
            || visibility.ValueKind != JsonValueKind.String
            || !PostVisibilityWire.TryParse(visibility.GetString(), out var parsedVisibility)
            || !TryImage(arguments, out var image))
        {
            return false;
        }

        parsed = new PostArguments(text.GetString()!, parsedVisibility, image);
        reason = null;
        return true;
    }

    // Path and hash together or not at all; the alt text only with them.
    private static bool TryImage(JsonElement arguments, out PostImageReference? image)
    {
        image = null;
        var hasPath = arguments.TryGetProperty("image_path", out var path);
        var hasHash = arguments.TryGetProperty("image_sha256", out var hash);
        var hasAlt = arguments.TryGetProperty("image_alt", out var alt);
        if (!hasPath && !hasHash)
        {
            return !hasAlt;
        }

        if (!hasPath || !hasHash
            || path.ValueKind != JsonValueKind.String || path.GetString()!.Length == 0
            || hash.ValueKind != JsonValueKind.String || !HashShape().IsMatch(hash.GetString()!))
        {
            return false;
        }

        string? altText = null;
        if (hasAlt)
        {
            if (alt.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            altText = alt.GetString()!;
            if (altText.EnumerateRunes().Count() > MaxAltChars || altText.Any(char.IsControl))
            {
                return false;
            }
        }

        image = new PostImageReference(path.GetString()!, hash.GetString()!, altText);
        return true;
    }

    [GeneratedRegex(@"\A[0-9a-f]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex HashShape();
}
