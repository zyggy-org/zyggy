namespace Zyggy.Core.Memory.Mirror;

/// <summary>The memory revision a snapshot is built from, and the date it is published (spec 47 "What a mirror is").</summary>
/// <param name="Sha">The memory commit (full sha).</param>
/// <param name="Date">The publish date.</param>
internal sealed record MirrorSource(string Sha, DateOnly Date);
