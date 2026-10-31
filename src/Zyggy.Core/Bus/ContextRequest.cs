using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using Zyggy.Core.Envelope;
using Zyggy.Core.Memory;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Bus;

/// <summary>Why a fact or scope was not accepted: the option or field and the detail (spec 12 AC-5, AC-12).</summary>
/// <param name="Field">The field (<c>scope</c>, <c>fact</c>, <c>to</c>).</param>
/// <param name="Detail">The detail, the <c>remember</c> verb's words.</param>
public sealed record ContextFactError(string Field, string Detail);

/// <summary>
/// What a context envelope carries (spec 12): the <c>scope</c> and the body lines, each already in the fact-line grammar, one fact
/// per line (founding spec §4).
/// </summary>
/// <param name="Scope">The envelope's <c>scope</c>.</param>
/// <param name="Lines">The body lines, without line endings.</param>
public sealed partial record ContextRequest(ContextScope Scope, IReadOnlyList<string> Lines)
{
    private const string ProjectPrefix = "project:";

    private const int MaxFactChars = 1000;

    /// <summary>
    /// Turns <c>--scope</c> and the stated fact into a one-line request: the scope <c>general</c> when absent, the fact collapsed to
    /// one line, 1–1,000 Unicode scalars, as <c>- [stated] &lt;date&gt;&lt;hint&gt;: &lt;fact&gt;</c>.
    /// </summary>
    /// <param name="scope">The <c>--scope</c> value, or <see langword="null"/>.</param>
    /// <param name="fact">The fact, or <see langword="null"/>.</param>
    /// <param name="utcDate">Today, UTC.</param>
    /// <param name="request">The request when accepted.</param>
    /// <returns><see langword="null"/> when accepted; otherwise the field and detail.</returns>
    public static ContextFactError? TryCreateStated(string? scope, string? fact, DateOnly utcDate, out ContextRequest? request)
    {
        request = null;
        ContextScope parsed;
        string hint;
        switch (scope)
        {
            case null or "general":
                parsed = new ContextScope(ContextScopeKind.General, null);
                hint = string.Empty;
                break;
            case "machine":
                parsed = new ContextScope(ContextScopeKind.Machine, null);
                hint = " (machine)";
                break;
            case var s when s.StartsWith(ProjectPrefix, StringComparison.Ordinal):
                var name = s[ProjectPrefix.Length..];
                if (!IsProjectLabel(name))
                {
                    return new ContextFactError("scope", "invalid project name in --scope");
                }

                parsed = new ContextScope(ContextScopeKind.Project, name);
                hint = $" ({s})";
                break;
            default:
                return new ContextFactError("scope", "--scope must be general, project:<name> or machine");
        }

        var collapsed = TextCollapse.Line(fact ?? string.Empty);
        if (collapsed.Length == 0)
        {
            return new ContextFactError("fact", "empty fact");
        }

        if (TextCollapse.CharCount(collapsed) > MaxFactChars)
        {
            return new ContextFactError("fact", $"fact longer than {MaxFactChars} characters");
        }

        var date = utcDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        request = new ContextRequest(parsed, [FactLine.Format("stated", hint, string.Empty, date, collapsed)]);
        return null;
    }

    /// <summary>Refuses the memory owner's own node as a sender: Central keeps its facts with <c>zyggy memory remember</c>.</summary>
    /// <param name="machine">This machine.</param>
    /// <returns><see langword="null"/> when the machine may send context; otherwise the refusal.</returns>
    public static ContextFactError? CheckSender(MachineName machine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        return machine == MachineName.Central ? new ContextFactError("to", "this machine is central; use zyggy memory remember") : null;
    }

    /// <summary>A project name as <c>--scope project:&lt;name&gt;</c> accepts it: <c>^[a-z0-9][a-z0-9-]*$</c>.</summary>
    internal static bool IsProjectLabel(string name) => ProjectLabel().IsMatch(name);

    /// <summary>The body: the lines joined with <c>\n</c> and one trailing <c>\n</c>, UTF-8 without BOM.</summary>
    internal byte[] Body() => new UTF8Encoding(false).GetBytes(string.Concat(Lines.Select(l => l + "\n")));

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ProjectLabel();
}
