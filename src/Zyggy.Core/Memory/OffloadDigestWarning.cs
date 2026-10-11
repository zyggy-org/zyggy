using System.Globalization;

namespace Zyggy.Core.Memory;

/// <summary>
/// The identity digest's offload line (spec 38 AC-23): <c>[warning] archive offload: &lt;n&gt; item(s) need attention — …</c> while any
/// item is parked or the last run failed as a whole; <c>&lt;n&gt;</c> is the parked items, or 1 for a failed run with none parked (plan
/// assumption A12).
/// </summary>
public static class OffloadDigestWarning
{
    /// <summary>Returns the warning line for the machine's offload state, or <see langword="null"/> when nothing needs attention.</summary>
    /// <param name="environment">The process environment (<c>ZYGGY_STATE_DIR</c>, <c>HOME</c>).</param>
    /// <returns>The line; <see langword="null"/> also when the state file is missing or unreadable — the digest never fails on it.</returns>
    public static string? Line(IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return OffloadState.Load(new ArchiveStatePaths(environment)).State is { } state ? Line(state) : null;
    }

    internal static string? Line(OffloadState state)
    {
        if (!state.NeedsAttention)
        {
            return null;
        }

        var count = Math.Max(state.Parked.Count, 1);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"[warning] archive offload: {count} item(s) need attention — zyggy memory archive list; runbook \"Offload upload failed\"");
    }
}
