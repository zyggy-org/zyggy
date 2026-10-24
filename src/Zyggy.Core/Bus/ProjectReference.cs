using System.Text.RegularExpressions;

namespace Zyggy.Core.Bus;

/// <summary>
/// A job's <c>project</c> (founding spec §4): a registry name or an absolute path, judged by <em>shape</em> on every OS (Central sends
/// Windows paths to the laptops) and never checked for existence — the node's pre-flight does that (deliverable 06).
/// </summary>
/// <param name="Value">The text as given.</param>
/// <param name="IsPath"><see langword="true"/> for an absolute path (POSIX or Windows drive), <see langword="false"/> for a registry name.</param>
public readonly partial record struct ProjectReference(string Value, bool IsPath)
{
    /// <summary>Parses <paramref name="text"/>; on refusal <paramref name="detail"/> says why in the words the CLI prints.</summary>
    /// <param name="text">The option value.</param>
    /// <param name="result">The reference when accepted.</param>
    /// <param name="detail">The refusal, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when accepted.</returns>
    public static bool TryParse(string? text, out ProjectReference result, out string? detail)
    {
        result = default;
        if (string.IsNullOrEmpty(text))
        {
            detail = "project is empty";
            return false;
        }

        if (text.Length > JobRequest.MaxProjectLength)
        {
            detail = "project is longer than 1024 characters";
            return false;
        }

        if (text.Any(char.IsControl))
        {
            detail = "project contains a control character";
            return false;
        }

        if (text.Split(['/', '\\']).Contains(".."))
        {
            detail = "project must not contain '..'";
            return false;
        }

        if (Label().IsMatch(text))
        {
            result = new ProjectReference(text, false);
            detail = null;
            return true;
        }

        if (text[0] == '/' || WindowsDrive().IsMatch(text))
        {
            result = new ProjectReference(text, true);
            detail = null;
            return true;
        }

        detail = "project must be a registry name ([a-z0-9-]) or an absolute path";
        return false;
    }

    [GeneratedRegex(@"^[a-z0-9-]{1,63}\z")]
    private static partial Regex Label();

    [GeneratedRegex(@"^[A-Za-z]:[\\/]")]
    private static partial Regex WindowsDrive();
}
