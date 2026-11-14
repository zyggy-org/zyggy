namespace Zyggy.Core.Configuration;

/// <summary>
/// The <c>agentCore</c> section of <c>node.json</c> (founding spec §9, §10 as amended by deliverable 15): where this machine's
/// <c>zyggy-core</c> checkout is, so the node can report the commit it has checked out. Nothing is pinned.
/// </summary>
/// <param name="Path">The checkout (an absolute path); <see langword="null"/> means the default of <c>zyggy setup</c> (<see cref="Setup.SetupPaths.DefaultCorePath(IReadOnlyDictionary{string, string?})"/>).</param>
public sealed record AgentCoreSettings(string? Path = null);
