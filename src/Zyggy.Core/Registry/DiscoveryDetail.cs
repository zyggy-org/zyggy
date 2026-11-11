namespace Zyggy.Core.Registry;

/// <summary>
/// How much a machine's registry file carries (<c>node.json</c> <c>discovery.detail</c>, founding spec §10 as amended by A14-5).
/// </summary>
public enum DiscoveryDetail
{
    /// <summary>Every field: path, agents, skills, summary.</summary>
    Full,

    /// <summary>Project names and default branches only; nothing under <c>.claude/</c> or <c>CLAUDE.md</c> is read (the work laptop, §8).</summary>
    Names,
}
