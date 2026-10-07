using System.Globalization;
using System.Text.RegularExpressions;

namespace Zyggy.Core.Brief;

/// <summary>A selection of Z items: every item, or the numbers in the order given (duplicates removed).</summary>
internal sealed record ZSelection(bool All, IReadOnlyList<int> Numbers);

/// <summary>
/// <c>Zn[,Zm…]</c>, <c>Zn-Zm</c> and <c>all</c> (the <c>Z</c> in either case; spaces after a comma allowed) — spec 35 AC-24. The numbers come
/// only from this argument and the item list, never from brief text. A number is 1..999 and a range at most 100 items.
/// </summary>
internal static partial class ZSelector
{
    private const int MaxNumber = 999;
    private const int MaxRange = 100;

    public static bool TryParse(string text, out ZSelection selection)
    {
        ArgumentNullException.ThrowIfNull(text);
        selection = new ZSelection(false, []);
        if (string.Equals(text, "all", StringComparison.OrdinalIgnoreCase))
        {
            selection = new ZSelection(true, []);
            return true;
        }

        var numbers = new List<int>();
        foreach (var part in text.Split(','))
        {
            var match = Part().Match(part.TrimStart(' '));
            if (!match.Success)
            {
                return false;
            }

            var from = int.Parse(match.Groups["from"].Value, CultureInfo.InvariantCulture);
            var to = match.Groups["to"].Success ? int.Parse(match.Groups["to"].Value, CultureInfo.InvariantCulture) : from;
            if (from < 1 || to < from || to > MaxNumber || to - from >= MaxRange)
            {
                return false;
            }

            numbers.AddRange(Enumerable.Range(from, to - from + 1).Where(n => !numbers.Contains(n)));
        }

        selection = new ZSelection(false, numbers);
        return true;
    }

    [GeneratedRegex("^[Zz](?<from>[0-9]{1,4})(?:-[Zz](?<to>[0-9]{1,4}))?$", RegexOptions.CultureInvariant)]
    private static partial Regex Part();
}
