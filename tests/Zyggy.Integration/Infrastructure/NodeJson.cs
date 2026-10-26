using System.Globalization;

using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;

namespace Zyggy.Integration.Infrastructure;

/// <summary>Writes a test <c>node.json</c> (spec 04 minimal schema, spec 07 service keys) into a scratch directory and returns its path.</summary>
internal static class NodeJson
{
    public static string Write(
        string directory,
        TenantId tenant,
        MachineName machine,
        KeyId? signWith,
        string busLocalPath,
        string remote,
        string secretsRoot,
        int pollSeconds = 1,
        string branch = "main",
        string? tenantOverride = null,
        bool omitTenant = false,
        string? claudePath = null,
        string? nodeDir = null,
        string? workHoursJson = null,
        int? maxConcurrentJobs = null,
        int? safetyFetchMinutes = null,
        string? logLevel = null,
        string? extraTopLevelJson = null,
        string secretsStore = "file")
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "node.json");
        string tenantLine = omitTenant ? string.Empty : string.Create(CultureInfo.InvariantCulture, $"  \"tenant\": \"{tenantOverride ?? tenant.Value}\",\n");
        string signLine = signWith is null ? string.Empty : string.Create(CultureInfo.InvariantCulture, $"  \"signWith\": \"{signWith}\",\n");
        string claudeLine = claudePath is null ? string.Empty : string.Create(CultureInfo.InvariantCulture, $"  \"claude\": {{ \"path\": \"{Slashes(claudePath)}\" }},\n");
        string nodeDirLine = nodeDir is null ? string.Empty : string.Create(CultureInfo.InvariantCulture, $"  \"nodeDir\": \"{Slashes(nodeDir)}\",\n");
        string workHoursLine = workHoursJson is null ? string.Empty : string.Create(CultureInfo.InvariantCulture, $"  \"workHours\": {workHoursJson},\n");
        string concurrencyLine = maxConcurrentJobs is null ? string.Empty : string.Create(CultureInfo.InvariantCulture, $"  \"maxConcurrentJobs\": {maxConcurrentJobs},\n");
        string safetyFetch = safetyFetchMinutes is null ? string.Empty : string.Create(CultureInfo.InvariantCulture, $", \"safetyFetchMinutes\": {safetyFetchMinutes}");
        string logLine = logLevel is null ? string.Empty : string.Create(CultureInfo.InvariantCulture, $"  \"log\": {{ \"level\": \"{logLevel}\" }},\n");
        string extraLine = extraTopLevelJson is null ? string.Empty : string.Create(CultureInfo.InvariantCulture, $"  {extraTopLevelJson},\n");
        string json = string.Create(CultureInfo.InvariantCulture, $$"""
            {
            {{tenantLine}}  "machine": "{{machine.Value}}",
            {{signLine}}{{claudeLine}}{{nodeDirLine}}{{workHoursLine}}{{concurrencyLine}}{{logLine}}{{extraLine}}  "bus": {
                "owner": "acme-org",
                "repo": "acme-bus",
                "branch": "{{branch}}",
                "localPath": "{{Slashes(busLocalPath)}}",
                "remote": "{{remote}}"
              },
              "poll": { "activeSeconds": {{pollSeconds}}, "idleSeconds": {{pollSeconds}}{{safetyFetch}} },
              "secrets": { "store": "{{secretsStore}}", "root": "{{Slashes(secretsRoot)}}" }
            }
            """);
        File.WriteAllText(path, json);
        return path;
    }

    private static string Slashes(string path) => path.Replace('\\', '/');
}
