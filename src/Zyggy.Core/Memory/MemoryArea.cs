namespace Zyggy.Core.Memory;

/// <summary>What part of the principal's memory tree a resolved path belongs to.</summary>
public enum MemoryArea
{
    /// <summary><c>profile.md</c> or <c>preferences.md</c>.</summary>
    Identity,

    /// <summary><c>agents.md</c>.</summary>
    Agents,

    /// <summary>A memory file <c>&lt;side&gt;/&lt;category&gt;/&lt;slug&gt;.md</c>.</summary>
    Durable,

    /// <summary>A category description <c>&lt;side&gt;/&lt;category&gt;/_index.md</c>.</summary>
    CategoryIndex,

    /// <summary>Anything under <c>daily/</c>.</summary>
    Daily,

    /// <summary>Anything under <c>inbox/</c>.</summary>
    Inbox,

    /// <summary>Anything under <c>auto/</c> (Claude Code auto memory).</summary>
    Auto,

    /// <summary>Anything under <c>.dream/</c> (ledger, quarantine, pending marker).</summary>
    Dream,

    /// <summary>The 27 layout's root <c>areas/</c>, <c>people/</c> or <c>topics/</c>, migrated once.</summary>
    Legacy,

    /// <summary>Any other path inside the principal directory, including side and category directories.</summary>
    Other,
}
