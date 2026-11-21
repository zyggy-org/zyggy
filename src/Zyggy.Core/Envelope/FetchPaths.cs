namespace Zyggy.Core.Envelope;

/// <summary>
/// The shape rules of a <c>fetch</c> job's fields (spec 59): the one place the sender (<c>zyggy submit --fetch</c>), the parser and the
/// node read them from, so they cannot drift.
/// </summary>
internal static class FetchPaths
{
    /// <summary>The longest <c>fetch_path</c>.</summary>
    public const int MaxLength = 200;

    /// <summary>
    /// A path the node may read inside the project: repository-relative, <c>/</c> separators, at most <see cref="MaxLength"/> characters,
    /// no backslash, colon or control character, and no empty, <c>.</c> or <c>..</c> segment (so neither absolute nor escaping the repository).
    /// </summary>
    public static bool IsSafe(string? path) =>
        path is { Length: > 0 and <= MaxLength }
        && path[0] != '/'
        && !path.Contains('\\', StringComparison.Ordinal)
        && !path.Contains(':', StringComparison.Ordinal)
        && !path.Any(char.IsControl)
        && path.Split('/').All(segment => segment.Length > 0 && segment != "." && segment != "..");

    /// <summary>A branch name a job may name (<c>base</c>, <c>fetch_ref</c>) or a mandate list: a cautious subset of git's ref-name rules.</summary>
    public static bool IsBranch(string text) =>
        text.Length is > 0 and <= 200
        && text[0] is not ('-' or '/' or '.')
        && text[^1] is not ('/' or '.')
        && !text.Contains("..", StringComparison.Ordinal)
        && !text.Contains("//", StringComparison.Ordinal)
        && !text.Contains("@{", StringComparison.Ordinal)
        && !text.Any(c => char.IsControl(c) || c is ' ' or '~' or '^' or ':' or '?' or '*' or '[' or '\\');

    /// <summary>A <c>file_name</c> of an attached file: one path segment of 1–200 characters, no separator, colon or control character, not <c>.</c> or <c>..</c>.</summary>
    public static bool IsFileName(string? name) =>
        name is { Length: > 0 and <= MaxLength }
        && name is not ("." or "..")
        && !name.Any(c => char.IsControl(c) || c is '/' or '\\' or ':');
}
