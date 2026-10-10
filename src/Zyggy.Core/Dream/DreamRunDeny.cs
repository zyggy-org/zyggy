using Zyggy.Core.Memory;
using Zyggy.Core.Models;

namespace Zyggy.Core.Dream;

/// <summary>
/// What every dream model session denies: the LinkedIn rules of every unattended run (spec 36 AC-8) and reading the principal's
/// <c>archive/</c> (spec 37 AC-21), although <c>--add-dir</c> grants the principal directory.
/// </summary>
internal static class DreamRunDeny
{
    public static IReadOnlyList<string> Rules(MemoryPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return [.. LinkedIn.LinkedInRunDeny.Rules, $"Read({ClaudeRules.Absolute(paths.PrincipalDirectory)}/archive/**)"];
    }
}
