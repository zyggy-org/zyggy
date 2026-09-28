using System.Diagnostics.CodeAnalysis;

namespace Zyggy.Core.Envelope;

/// <summary>The kind of a context envelope's <c>scope</c> (founding spec §4).</summary>
public enum ContextScopeKind
{
    /// <summary><c>project:&lt;name&gt;</c>.</summary>
    Project,

    /// <summary><c>machine</c>.</summary>
    Machine,

    /// <summary><c>general</c>.</summary>
    General,
}

/// <summary>
/// A context envelope's <c>scope</c>: <c>project:&lt;name&gt;</c>, <c>machine</c> or <c>general</c> (founding spec §4).
/// </summary>
/// <param name="Kind">The scope kind.</param>
/// <param name="ProjectName">The project name for <see cref="ContextScopeKind.Project"/>; otherwise <see langword="null"/>.</param>
public sealed record ContextScope(ContextScopeKind Kind, string? ProjectName)
{
    private const string ProjectPrefix = "project:";

    /// <summary>Parses a scope.</summary>
    /// <param name="value">The scope text.</param>
    /// <returns>The scope.</returns>
    /// <exception cref="FormatException">Thrown when <paramref name="value"/> is not a valid scope.</exception>
    public static ContextScope Parse(string value) =>
        TryParse(value, out ContextScope? result)
            ? result
            : throw new FormatException("A scope must be project:<name>, machine or general.");

    /// <summary>Tries to parse a scope.</summary>
    /// <param name="value">The scope text, or <see langword="null"/>.</param>
    /// <param name="result">The scope when the text is valid; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is a valid scope.</returns>
    public static bool TryParse(string? value, [NotNullWhen(true)] out ContextScope? result)
    {
        result = value switch
        {
            null => null,
            "machine" => new ContextScope(ContextScopeKind.Machine, null),
            "general" => new ContextScope(ContextScopeKind.General, null),
            _ when value.StartsWith(ProjectPrefix, StringComparison.Ordinal) && IsProjectName(value.AsSpan(ProjectPrefix.Length)) =>
                new ContextScope(ContextScopeKind.Project, value[ProjectPrefix.Length..]),
            _ => null,
        };
        return result is not null;
    }

    /// <summary>Returns the wire form.</summary>
    /// <returns>The scope text.</returns>
    public override string ToString() => Kind switch
    {
        ContextScopeKind.Project => ProjectPrefix + ProjectName,
        _ => EnvelopeWire.ToWire(Kind),
    };

    private static bool IsProjectName(ReadOnlySpan<char> name)
    {
        if (name.IsEmpty)
        {
            return false;
        }

        foreach (char c in name)
        {
            if (c == ':' || char.IsWhiteSpace(c))
            {
                return false;
            }
        }

        return true;
    }
}
