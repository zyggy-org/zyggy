using Zyggy.Core.Memory;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Dream;

/// <summary>Where and for whom a dream run works (spec 28 Configuration).</summary>
/// <param name="MemoryRoot">The memory repository's working tree; the principal's tree is <c>&lt;root&gt;/&lt;tenant&gt;/&lt;user&gt;</c>.</param>
/// <param name="Principal">The owner whose memory this is.</param>
/// <param name="TimeZone">The zone of every local date (commit subject, <c>updated</c>, rollup).</param>
/// <param name="StateDirectory">Lock, request file, run log, batch-size state and run directories.</param>
/// <param name="SecretPatternsPath">The secret-pattern file the checks use (fail closed when missing).</param>
/// <param name="InstanceDirectory">The instance directory holding <c>zyggy.json</c> and <c>dream.json</c>, when set.</param>
/// <param name="Version">The running <c>zyggy</c> version.</param>
public sealed record DreamEnvironment(
    string MemoryRoot,
    Principal Principal,
    TimeZoneInfo TimeZone,
    string StateDirectory,
    string SecretPatternsPath,
    string? InstanceDirectory,
    string Version)
{
    /// <summary>Gets the principal's memory paths.</summary>
    public MemoryPaths Paths => new(MemoryRoot, Principal);
}
