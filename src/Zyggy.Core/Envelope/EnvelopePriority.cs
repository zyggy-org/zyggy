namespace Zyggy.Core.Envelope;

/// <summary>The envelope <c>priority</c> field (founding spec §4); default <see cref="Normal"/>.</summary>
public enum EnvelopePriority
{
    /// <summary><c>low</c>.</summary>
    Low,

    /// <summary><c>normal</c> (the default when the field is absent).</summary>
    Normal,

    /// <summary><c>high</c>: may run outside work hours (§6).</summary>
    High,
}
