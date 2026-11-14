using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Jobs;

/// <summary>
/// The one builder of transcript paths (founding spec §6 Streaming and limits, §8 Audit): <c>&lt;nodeDir&gt;/runs/&lt;tenant&gt;/&lt;job id&gt;.jsonl</c>
/// on the executing machine, never on the bus. The tenant is part of the path (§14).
/// </summary>
internal static class TranscriptPaths
{
    /// <summary><c>%LOCALAPPDATA%\zyggy</c> on Windows, <c>~/.local/share/zyggy</c> elsewhere (spec 06 Configuration, <c>nodeDir</c>).</summary>
    public static string DefaultNodeDir() =>
        Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "zyggy");

    public static string TenantDirectory(string nodeDir, TenantId tenant)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeDir);
        ArgumentNullException.ThrowIfNull(tenant);
        return Path.Join(nodeDir, "runs", tenant.Value);
    }

    public static string For(string nodeDir, TenantId tenant, EnvelopeId id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return Path.Join(TenantDirectory(nodeDir, tenant), id.Value + ".jsonl");
    }

    /// <summary>The run's blocked-calls file (spec 15): <c>&lt;nodeDir&gt;/runs/&lt;tenant&gt;/&lt;job id&gt;.blocked</c>, one line per call the hook refused.</summary>
    public static string BlockedFor(string nodeDir, TenantId tenant, EnvelopeId id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return Path.Join(TenantDirectory(nodeDir, tenant), id.Value + ".blocked");
    }

    /// <summary>The <c>zyggy</c> binary beside the running process (spec 15: both binaries ship in one release folder).</summary>
    public static string DefaultHookExecutable() =>
        Path.Join(Path.GetDirectoryName(Environment.ProcessPath), OperatingSystem.IsWindows() ? "zyggy.exe" : "zyggy");

    /// <summary>The folder a worktree job runs in (spec 16 D5): <c>&lt;nodeDir&gt;/worktrees/&lt;tenant&gt;/&lt;job id&gt;</c>, never beside the project.</summary>
    public static string WorktreeFor(string nodeDir, TenantId tenant, EnvelopeId id)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeDir);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(id);
        return Path.Join(nodeDir, "worktrees", tenant.Value, id.Value);
    }
}
