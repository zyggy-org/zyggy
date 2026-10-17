using System.Text.RegularExpressions;

using Zyggy.Core.Models;

namespace Zyggy.Core.Jobs;

/// <summary>
/// The §6 report summary: the REPORT section of the result text (from the first heading line <c>REPORT</c>, any Markdown level, to
/// the end), else the whole result text, else the last assistant text, else <c>(no output)</c> — at most 2,000 UTF-16 code units,
/// cut with a trailing <c>…</c> and never inside a surrogate pair.
/// </summary>
internal static partial class ReportSummary
{
    public const int MaxCharacters = 2000;

    public const string NoOutput = "(no output)";

    public static string Extract(ModelRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.ResultText?.Replace("\r", "", StringComparison.Ordinal) is { Length: > 0 } text)
        {
            var heading = ReportHeading().Match(text);
            return Truncate(heading.Success ? text[heading.Index..] : text);
        }

        return result.LastAssistantText is { Length: > 0 } last ? Truncate(last) : NoOutput;
    }

    public static string Truncate(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length <= MaxCharacters)
        {
            return text;
        }

        var keep = MaxCharacters - 1;
        if (char.IsHighSurrogate(text[keep - 1]))
        {
            keep--;
        }

        return string.Concat(text.AsSpan(0, keep), "…");
    }

    [GeneratedRegex(@"^(#+[ \t]*)?REPORT[ \t]*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ReportHeading();
}
