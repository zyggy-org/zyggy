namespace Zyggy.Core.Dream;

/// <summary>
/// The rule a dream batch or run broke (spec 28, closed; one per AC-15 rule). Wire strings come from <see cref="DreamCheckWire"/> only.
/// </summary>
public enum DreamCheck
{
    /// <summary>A target outside the memory files the model may write.</summary>
    PathRefused,

    /// <summary>A file name that is not a valid slug.</summary>
    SlugInvalid,

    /// <summary>A slug already used in the principal's tree.</summary>
    SlugDuplicate,

    /// <summary>A bad, unknown or empty category.</summary>
    CategoryInvalid,

    /// <summary>Too many categories on a side or in a run.</summary>
    CategoryCap,

    /// <summary>A line or description that breaks the memory line format.</summary>
    FormatInvalid,

    /// <summary>A tag other than <c>[stated]</c> or <c>[observed]</c>.</summary>
    ForeignTag,

    /// <summary>An <c>[observed]</c> line without a bracketed provenance.</summary>
    ProvenanceMissing,

    /// <summary>A <c>[stated]</c> line not backed by a <c>[stated]</c> source.</summary>
    TagUpgrade,

    /// <summary>An <c>[observed]</c> line added to an identity file.</summary>
    IdentityObserved,

    /// <summary>An identity file losing too many lines.</summary>
    IdentityShrink,

    /// <summary>A line, description or alias matching a secret pattern.</summary>
    SecretPattern,

    /// <summary>An e-mail address or phone number.</summary>
    ContactDetail,

    /// <summary>A <c>remove</c> or <c>replace</c> of a line that does not exist.</summary>
    EditMismatch,

    /// <summary>A batch that removes too much.</summary>
    RemovalLimit,

    /// <summary>An input line without exactly one disposition.</summary>
    Coverage,

    /// <summary>A <c>[stated]</c> inbox line dropped.</summary>
    StatedDropped,

    /// <summary>A disposition whose target does not hold the fact's provenance.</summary>
    FactNotFound,

    /// <summary>A target changed by someone else since the snapshot.</summary>
    ConcurrentEdit,

    /// <summary>An invalid compression.</summary>
    CompressRejected,

    /// <summary>An invalid layout migration.</summary>
    MigrationRejected,

    /// <summary>A run that removes too much of the durable memory.</summary>
    RunRemovalLimit,

    /// <summary>An inbox deletion not in the rollup plan.</summary>
    UnfiledDeletion,

    /// <summary>A path of a dead run edited by someone else since.</summary>
    DirtyPending,
}

/// <summary>The single mapping between <see cref="DreamCheck"/> and its snake_case wire strings.</summary>
public static class DreamCheckWire
{
    /// <summary>Returns the wire string of a check.</summary>
    /// <param name="check">The check.</param>
    /// <returns>The snake_case name, for example <c>path_refused</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for an undefined member.</exception>
    public static string ToWire(DreamCheck check) => check switch
    {
        DreamCheck.PathRefused => "path_refused",
        DreamCheck.SlugInvalid => "slug_invalid",
        DreamCheck.SlugDuplicate => "slug_duplicate",
        DreamCheck.CategoryInvalid => "category_invalid",
        DreamCheck.CategoryCap => "category_cap",
        DreamCheck.FormatInvalid => "format_invalid",
        DreamCheck.ForeignTag => "foreign_tag",
        DreamCheck.ProvenanceMissing => "provenance_missing",
        DreamCheck.TagUpgrade => "tag_upgrade",
        DreamCheck.IdentityObserved => "identity_observed",
        DreamCheck.IdentityShrink => "identity_shrink",
        DreamCheck.SecretPattern => "secret_pattern",
        DreamCheck.ContactDetail => "contact_detail",
        DreamCheck.EditMismatch => "edit_mismatch",
        DreamCheck.RemovalLimit => "removal_limit",
        DreamCheck.Coverage => "coverage",
        DreamCheck.StatedDropped => "stated_dropped",
        DreamCheck.FactNotFound => "fact_not_found",
        DreamCheck.ConcurrentEdit => "concurrent_edit",
        DreamCheck.CompressRejected => "compress_rejected",
        DreamCheck.MigrationRejected => "migration_rejected",
        DreamCheck.RunRemovalLimit => "run_removal_limit",
        DreamCheck.UnfiledDeletion => "unfiled_deletion",
        DreamCheck.DirtyPending => "dirty_pending",
        _ => throw new ArgumentOutOfRangeException(nameof(check)),
    };

    /// <summary>Maps a wire string to a check.</summary>
    /// <param name="wire">The wire string.</param>
    /// <param name="check">The check when recognised.</param>
    /// <returns><see langword="true"/> when <paramref name="wire"/> is an exact wire string.</returns>
    public static bool TryFromWire(string? wire, out DreamCheck check)
    {
        foreach (var member in Enum.GetValues<DreamCheck>())
        {
            if (string.Equals(ToWire(member), wire, StringComparison.Ordinal))
            {
                check = member;
                return true;
            }
        }

        check = default;
        return false;
    }
}
