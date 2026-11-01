namespace Zyggy.Core.Memory;

/// <summary>
/// The text limits of an archive item, shared by <c>archive add</c> (spec 37) and the <c>item</c> envelope (spec 41 AC-2), so the laptop,
/// the bus and Central agree on one number each.
/// </summary>
internal static class ArchiveLimits
{
    /// <summary>The longest <c>name</c>, in Unicode scalar values.</summary>
    public const int NameMax = 100;

    /// <summary>The longest <c>description</c>, in Unicode scalar values.</summary>
    public const int DescriptionMax = 149;

    /// <summary>The longest <c>source_name</c>, in UTF-16 characters.</summary>
    public const int SourceNameMax = 100;
}
