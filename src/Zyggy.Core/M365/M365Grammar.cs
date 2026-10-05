using System.Text.RegularExpressions;

namespace Zyggy.Core.M365;

/// <summary>
/// The value grammars of the template's <c>m365-lib.sh</c>. Anchored with <c>\A…\z</c>: bash's <c>=~</c> anchors match only the
/// ends of the string, never before a trailing newline as .NET's <c>$</c> would.
/// </summary>
internal static partial class M365Grammar
{
    [GeneratedRegex(@"\A[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\z", RegexOptions.CultureInvariant)]
    public static partial Regex Guid();

    [GeneratedRegex(@"\A[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\z", RegexOptions.CultureInvariant)]
    public static partial Regex Upn();

    [GeneratedRegex(@"\A[0-9]{4}-[0-9]{2}-[0-9]{2}\z", RegexOptions.CultureInvariant)]
    public static partial Regex Date();

    [GeneratedRegex(@"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z\z", RegexOptions.CultureInvariant)]
    public static partial Regex Iso();

    [GeneratedRegex(@"\A[A-Za-z0-9_=-]{1,512}\z", RegexOptions.CultureInvariant)]
    public static partial Regex Id();

    [GeneratedRegex(@"\A[A-Za-z0-9_!.=-]{1,512}\z", RegexOptions.CultureInvariant)]
    public static partial Regex DriveId();

    [GeneratedRegex(@"\A[A-Za-z0-9_!.=-]{1,200}\z", RegexOptions.CultureInvariant)]
    public static partial Regex ItemId();

    [GeneratedRegex(@"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z(\|[A-Za-z0-9_!.=-]{1,200})?\z", RegexOptions.CultureInvariant)]
    public static partial Regex Cursor();

    /// <summary>A state key's argument (folder, drive): <c>state.sh</c>'s <c>ZY_STATE_ARG_RE</c>.</summary>
    [GeneratedRegex(@"\A[A-Za-z0-9!_=-]{1,200}\z", RegexOptions.CultureInvariant)]
    public static partial Regex StateArgument();
}
