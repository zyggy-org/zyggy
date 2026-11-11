using System.Text.RegularExpressions;

namespace Zyggy.Core.Jobs;

/// <summary>
/// The one rule for a subagent or skill name that may name a file under <c>.claude/</c>: <c>[A-Za-z0-9._-]+</c>. Shared by the §6
/// pre-flight (step 2) and the discovery scanner (spec 14).
/// </summary>
internal static partial class AgentNames
{
    internal static bool IsSafe(string name) => SafeName().IsMatch(name);

    [GeneratedRegex(@"^[A-Za-z0-9._-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex SafeName();
}
