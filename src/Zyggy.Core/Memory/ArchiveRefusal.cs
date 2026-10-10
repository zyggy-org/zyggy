namespace Zyggy.Core.Memory;

/// <summary>Why <c>zyggy memory archive add</c> or <c>remove</c> refused (spec 37 Contracts); exit 2, nothing written.</summary>
public enum ArchiveRefusal
{
    /// <summary><c>ZYGGY_HOOKS=off</c>: an unattended run may not archive.</summary>
    Unattended,

    /// <summary>The source path is missing, not a regular file, reached through a symbolic link, inside memory or under a denied location.</summary>
    SourceRefused,

    /// <summary>The bytes are not a recognised type.</summary>
    TypeRefused,

    /// <summary>The type is recognised but not in the instance's allow-list.</summary>
    TypeNotAllowed,

    /// <summary>A zero-byte file.</summary>
    Empty,

    /// <summary>The item is above <c>item_max_bytes</c>.</summary>
    TooLarge,

    /// <summary>The project's archive would exceed <c>project_max_bytes</c>.</summary>
    ProjectCap,

    /// <summary>The whole archive would exceed <c>total_max_bytes</c>.</summary>
    TotalCap,

    /// <summary>A text line, the name or the description matches a secret pattern.</summary>
    SecretPattern,

    /// <summary>The name or the description contains an e-mail address or phone number.</summary>
    ContactDetail,

    /// <summary>An item with this slug already exists in the project.</summary>
    SlugTaken,

    /// <summary>The item to remove does not exist.</summary>
    NotFound,
}

/// <summary>The single mapping between <see cref="ArchiveRefusal"/> and its snake_case reason on stderr.</summary>
public static class ArchiveRefusalWire
{
    /// <summary>Returns the reason string of a member.</summary>
    /// <param name="refusal">The refusal.</param>
    /// <returns>The snake_case reason.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for an undefined member.</exception>
    public static string ToWire(ArchiveRefusal refusal) => refusal switch
    {
        ArchiveRefusal.Unattended => "unattended",
        ArchiveRefusal.SourceRefused => "source_refused",
        ArchiveRefusal.TypeRefused => "type_refused",
        ArchiveRefusal.TypeNotAllowed => "type_not_allowed",
        ArchiveRefusal.Empty => "empty",
        ArchiveRefusal.TooLarge => "too_large",
        ArchiveRefusal.ProjectCap => "project_cap",
        ArchiveRefusal.TotalCap => "total_cap",
        ArchiveRefusal.SecretPattern => "secret_pattern",
        ArchiveRefusal.ContactDetail => "contact_detail",
        ArchiveRefusal.SlugTaken => "slug_taken",
        ArchiveRefusal.NotFound => "not_found",
        _ => throw new ArgumentOutOfRangeException(nameof(refusal)),
    };

    /// <summary>Maps a reason string to a member.</summary>
    /// <param name="wire">The reason string.</param>
    /// <param name="refusal">The member when recognised.</param>
    /// <returns><see langword="true"/> for exactly one of the strings <see cref="ToWire"/> produces.</returns>
    public static bool TryFromWire(string? wire, out ArchiveRefusal refusal)
    {
        foreach (var member in Enum.GetValues<ArchiveRefusal>())
        {
            if (string.Equals(ToWire(member), wire, StringComparison.Ordinal))
            {
                refusal = member;
                return true;
            }
        }

        refusal = default;
        return false;
    }
}
