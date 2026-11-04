using System.Globalization;

using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Setup;

/// <summary>
/// One run of <c>zyggy setup</c> (spec 42 Contracts): the <c>node.json</c> to complete, the tenant and machine the installer was given, the
/// <c>zyggy-core</c> checkout and the three directories the run writes under, and the instant that stamps every backup.
/// </summary>
/// <param name="ConfigPath">The <c>node.json</c> to complete (resolved like every verb's <c>--config</c>).</param>
/// <param name="Tenant">The tenant the installer was given; <c>node.json</c> must name the same.</param>
/// <param name="Machine">The machine the installer was given; <c>node.json</c> must name the same.</param>
/// <param name="SignWith">The key id added as <c>signWith</c> when <c>node.json</c> has none; the existing value always wins.</param>
/// <param name="CorePath">The absolute path of the <c>zyggy-core</c> checkout.</param>
/// <param name="CoreRemote">The <c>zyggy-core</c> remote (<see cref="DefaultCoreRemote"/> unless a test or a fork names another).</param>
/// <param name="ClaudeDir">The user-scope Claude Code directory (<c>--claude-dir</c>, else <c>CLAUDE_CONFIG_DIR</c>, else <c>&lt;UserProfile&gt;/.claude</c>).</param>
/// <param name="UserProfile">The user's profile directory (where <c>.local/bin/claude.exe</c> is looked for).</param>
/// <param name="LocalAppData">The local application data directory (skill backups, the spike file store).</param>
/// <param name="Now">The instant of the run; every backup of this run carries its stamp.</param>
public sealed record SetupRequest(
    string ConfigPath,
    TenantId Tenant,
    MachineName Machine,
    KeyId? SignWith,
    string CorePath,
    Uri CoreRemote,
    string ClaudeDir,
    string UserProfile,
    string LocalAppData,
    DateTimeOffset Now)
{
    /// <summary>The public template repository every laptop checks out (spec 42 Contracts).</summary>
    public const string DefaultCoreRemote = "https://github.com/zyggy-org/zyggy-core.git";

    /// <summary>Gets the backup suffix of this run: <see cref="Now"/> in UTC as <c>yyyyMMddTHHmmssZ</c>.</summary>
    public string BackupStamp => Now.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
}
