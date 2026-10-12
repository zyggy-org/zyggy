using System.Globalization;

using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;

namespace Zyggy.Integration.Infrastructure;

/// <summary>Writes a test <c>node.json</c> (spec 04 minimal schema) into a scratch directory and returns its path.</summary>
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
        bool omitTenant = false)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "node.json");
        string tenantLine = omitTenant ? string.Empty : string.Create(CultureInfo.InvariantCulture, $"  \"tenant\": \"{tenantOverride ?? tenant.Value}\",\n");
        string signLine = signWith is null ? string.Empty : string.Create(CultureInfo.InvariantCulture, $"  \"signWith\": \"{signWith}\",\n");
        string json = string.Create(CultureInfo.InvariantCulture, $$"""
            {
            {{tenantLine}}  "machine": "{{machine.Value}}",
            {{signLine}}  "bus": {
                "owner": "acme-org",
                "repo": "acme-bus",
                "branch": "{{branch}}",
                "localPath": "{{busLocalPath.Replace('\\', '/')}}",
                "remote": "{{remote}}"
              },
              "poll": { "activeSeconds": {{pollSeconds}}, "idleSeconds": {{pollSeconds}} },
              "secrets": { "root": "{{secretsRoot.Replace('\\', '/')}}" }
            }
            """);
        File.WriteAllText(path, json);
        return path;
    }
}
