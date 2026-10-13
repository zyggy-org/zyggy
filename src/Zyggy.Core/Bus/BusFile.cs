using Zyggy.Core.Envelope;

namespace Zyggy.Core.Bus;

/// <summary>A file in this node's job inbox (<c>jobs/&lt;ULID&gt;.md</c>, founding spec §4 write rules).</summary>
/// <param name="RelativePath">The repository-relative path, from <see cref="BusPaths"/>.</param>
/// <param name="Id">The file-name ULID; synthetic when the file name is not one (such a file is rejected as malformed).</param>
public sealed record BusFile(string RelativePath, EnvelopeId Id);
