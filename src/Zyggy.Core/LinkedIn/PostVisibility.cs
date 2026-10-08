namespace Zyggy.Core.LinkedIn;

/// <summary>Who sees a post (spec 36 MCP tool): the wire values are <c>PUBLIC</c> and <c>CONNECTIONS</c>.</summary>
internal enum PostVisibility
{
    /// <summary><c>PUBLIC</c>: anyone on or off LinkedIn.</summary>
    Public,

    /// <summary><c>CONNECTIONS</c>: the owner's first-degree connections.</summary>
    Connections,
}

/// <summary>The wire values of <see cref="PostVisibility"/>.</summary>
internal static class PostVisibilityWire
{
    public static string Wire(this PostVisibility visibility) => visibility switch
    {
        PostVisibility.Public => "PUBLIC",
        PostVisibility.Connections => "CONNECTIONS",
        _ => throw new ArgumentOutOfRangeException(nameof(visibility), visibility, null),
    };

    public static bool TryParse(string? value, out PostVisibility visibility)
    {
        (var ok, visibility) = value switch
        {
            "PUBLIC" => (true, PostVisibility.Public),
            "CONNECTIONS" => (true, PostVisibility.Connections),
            _ => (false, PostVisibility.Public),
        };
        return ok;
    }
}
