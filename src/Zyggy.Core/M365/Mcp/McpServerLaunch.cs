using Zyggy.Core.M365.Tools;
using Zyggy.Core.Verbs;

namespace Zyggy.Core.M365.Mcp;

/// <summary>The server to start: its path, its argv after the path, and its whole environment.</summary>
internal sealed record LaunchPlan(string ServerPath, IReadOnlyList<string> Argv, IReadOnlyDictionary<string, string> Environment);

/// <summary>A launch plan, or the exit-3 refusal.</summary>
internal sealed record LaunchPlanResult(LaunchPlan? Plan, int Exit, string? Error);

/// <summary>
/// How the m365 MCP server is started — <c>mcp-server.sh</c> (spec 23 D8, spec 33 AC-25): the reviewed
/// <c>@softeria/ms-365-mcp-server</c> under <c>~/.local</c> only, in org mode on loopback HTTP, with exactly the ten contracted
/// variables. No token: every request carries its own bearer from the headers helper. Never touches the key.
/// </summary>
internal static class McpServerLaunch
{
    public const string ServerName = "ms-365-mcp-server";
    private const string InstallHint = "runbook 13 \"Install or upgrade the MCP server\"";

    public static LaunchPlanResult Plan(IReadOnlyDictionary<string, string?> environment, M365Environment instance, M365Configuration configuration, M365ToolPartition partition)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(partition);
        if (M365Environment.Port(environment, out var portError) is not { } port)
        {
            return new LaunchPlanResult(null, 3, portError);
        }

        if (ProgramLocator.UserProgram(ServerName, environment) is not { } found)
        {
            return new LaunchPlanResult(null, 3, $"{ServerName} not found — {InstallHint}");
        }

        // The pinned package is installed with npm's prefix ~/.local (runbook 13); a server anywhere else is not the reviewed one.
        var home = environment.TryGetValue("HOME", out var h) && !string.IsNullOrEmpty(h) ? h : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var real = PathResolution.RealPath(found);
        var homeReal = PathResolution.RealPath(home);
        if (!real.StartsWith(Path.Join(homeReal, ".local") + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return new LaunchPlanResult(null, 3, $"{ServerName} at {real} is not under {home}/.local — {InstallHint}");
        }

        if (!File.Exists(real) || (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(real) & UnixFileMode.UserExecute) == 0))
        {
            return new LaunchPlanResult(null, 3, $"{ServerName} at {real} is not executable — {InstallHint}");
        }

        var listen = $"127.0.0.1:{port}";
        var variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = $"/usr/bin:/bin:{home}/.local/bin",
            ["HOME"] = home,
            ["LC_ALL"] = "C",
            ["NODE_OPTIONS"] = "--max-old-space-size=512",
            ["MS365_MCP_CLIENT_ID"] = configuration.ClientId,
            ["MS365_MCP_TENANT_ID"] = configuration.TenantId,
            ["MS365_MCP_ORG_MODE"] = "1",
            ["MS365_MCP_USE_KEYTAR"] = "0",
            ["MS365_MCP_TOKEN_CACHE_PATH"] = Path.Join(instance.Paths.StateDirectory, "never-written.json"),
            ["ENABLED_TOOLS"] = partition.EnabledToolsRegex,
        };
        return new LaunchPlanResult(
            new LaunchPlan(real, ["--org-mode", "--http", listen, "--http-local-file-tools", "--no-dynamic-registration"], variables), 0, null);
    }

    /// <summary>
    /// <c>zy_m365_download_root</c>: the shared download root, created 0700 when missing; an error when it is not a writable directory
    /// (or is a symbolic link).
    /// </summary>
    public static string? EnsureDownloadRoot(string root)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        try
        {
            if (!Directory.Exists(root) && !File.Exists(root) && new FileInfo(root).LinkTarget is null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(root)!);
                if (OperatingSystem.IsWindows())
                {
                    Directory.CreateDirectory(root);
                }
                else
                {
                    Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Reported below.
        }

        var info = new DirectoryInfo(root);
        var writable = info.Exists && info.LinkTarget is null
            && (OperatingSystem.IsWindows() || (File.GetUnixFileMode(root) & UnixFileMode.UserWrite) != 0);
        return writable ? null : $"configuration error: the download root {root} is not a writable directory — runbook 13 \"MCP server down\"";
    }
}
