using System.Globalization;

using Zyggy.Core.Memory;

namespace Zyggy.Core.LinkedIn;

/// <summary>
/// The local checks on a post's text before any request (spec 36 AC-4, OQ-5): the first reason of empty, too long, a control character
/// other than <c>\n</c>, a secret pattern, an e-mail address, a phone number. URLs and hashtags are allowed. A reason never holds the
/// matched text.
/// </summary>
internal static class PostPolicy
{
    /// <summary>The reasons whose row keeps no text (spec 36 Assumption 2): what matched must not be stored.</summary>
    public static bool IsContentRefusal(string reason) =>
        reason is "control character" or "e-mail address" or "phone number" || reason.StartsWith("secret pattern ", StringComparison.Ordinal);

    public static string? Check(string text, int maxChars, SecretPatterns patterns)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(patterns);
        if (string.IsNullOrWhiteSpace(text))
        {
            return "empty";
        }

        var characters = text.EnumerateRunes().Count();
        if (characters > maxChars)
        {
            return $"too long ({characters.ToString(CultureInfo.InvariantCulture)} > {maxChars.ToString(CultureInfo.InvariantCulture)})";
        }

        if (text.Any(c => char.IsControl(c) && c != '\n'))
        {
            return "control character";
        }

        if (patterns.TryMatch(text, out var name))
        {
            return "secret pattern " + name;
        }

        if (FactValidator.HoldsEmail(text))
        {
            return "e-mail address";
        }

        return ContactDetailPatterns.Contains(text) ? "phone number" : null;
    }
}
