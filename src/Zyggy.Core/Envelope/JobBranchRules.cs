using System.Text.RegularExpressions;

namespace Zyggy.Core.Envelope;

/// <summary>
/// The shape rules of the job fields <c>branch</c> and <c>description</c> (spec 53, founding spec §4 "Job-specific fields"). The single
/// source of these rules: the parser applies them, and through it the envelope factory and the submitter's read-back.
/// </summary>
internal static partial class JobBranchRules
{
    /// <summary>The <c>branch</c> length cap.</summary>
    internal const int MaxBranchLength = 100;

    /// <summary>The <c>description</c> length cap, in characters (Unicode scalar values).</summary>
    internal const int MaxDescriptionLength = 200;

    /// <summary>The rule text a refused <c>branch</c> is reported with (never the value).</summary>
    internal const string BranchRule =
        "must be a branch name of 1–100 characters of A–Z a–z 0–9 . _ / -, without an empty segment, a segment starting with . or - or ending in .lock, "
        + "'..', a trailing / or ., and not HEAD, zyggy/… or refs/…";

    /// <summary>The rule text a refused <c>description</c> is reported with (never the value).</summary>
    internal const string DescriptionRule = "must be one line of 1–200 characters without control characters";

    /// <summary>Whether <paramref name="branch"/> has the §4 shape of a job <c>branch</c>.</summary>
    /// <param name="branch">The field text.</param>
    /// <returns><see langword="true"/> when the name is acceptable.</returns>
    internal static bool IsValidBranch(string branch) =>
        BranchCharacters().IsMatch(branch)
        && !branch.Contains("..", StringComparison.Ordinal)
        && !branch.EndsWith('.')
        && !string.Equals(branch, "HEAD", StringComparison.Ordinal)
        && !branch.StartsWith("zyggy/", StringComparison.OrdinalIgnoreCase)
        && !branch.StartsWith("refs/", StringComparison.OrdinalIgnoreCase)
        && branch.Split('/').All(IsValidSegment);

    /// <summary>Whether <paramref name="description"/> has the §4 shape of a job <c>description</c>.</summary>
    /// <param name="description">The field text.</param>
    /// <returns><see langword="true"/> when the text is acceptable.</returns>
    internal static bool IsValidDescription(string description) =>
        !string.IsNullOrWhiteSpace(description)
        && !description.Any(char.IsControl)
        && description.EnumerateRunes().Count() <= MaxDescriptionLength;

    // An empty segment also covers a leading, trailing or doubled '/'.
    private static bool IsValidSegment(string segment) =>
        segment.Length > 0
        && segment[0] is not '.' and not '-'
        && !segment.EndsWith(".lock", StringComparison.Ordinal);

    [GeneratedRegex(@"^[A-Za-z0-9._/-]{1,100}\z")]
    private static partial Regex BranchCharacters();
}
