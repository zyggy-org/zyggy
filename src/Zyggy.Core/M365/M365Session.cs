using Zyggy.Core.M365.Graph;
using Zyggy.Core.Memory;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.M365;

/// <summary>
/// What a Graph verb needs after its arguments, loaded in <c>graph.sh</c>'s order: the principal (<c>zy_require_config</c>), the instance,
/// <c>instance/m365.json</c> (base keys only for <c>cert-init</c>), the secret patterns. Messages are without the caller's prefix.
/// </summary>
internal sealed record M365Session(
    MemoryPaths Memory,
    TimeZoneInfo TimeZone,
    M365Environment Instance,
    M365Configuration Configuration,
    string? Warning,
    SecretPatterns Patterns)
{
    public TenantId Tenant => Memory.Principal.Tenant;

    public static (M365Session? Session, int Exit, string? Error) Load(M365VerbContext context, bool baseOnly)
    {
        ArgumentNullException.ThrowIfNull(context);
        var memory = MemoryEnvironment.Resolve(context.Environment, context.FindTimeZone);
        if (memory.Error is not null)
        {
            return (null, 3, "configuration error: " + memory.Error);
        }

        var instance = M365Environment.Load(context.Environment);
        if (instance.Environment is not { } m365)
        {
            return (null, 3, instance.Error);
        }

        var today = DateOnly.FromDateTime(context.Clock.GetUtcNow().UtcDateTime);
        var configuration = M365Configuration.Load(m365.ConfigPath, baseOnly, today, id => IsKnownTimeZone(context, id));
        if (configuration.Configuration is not { } config)
        {
            return (null, 3, configuration.Error);
        }

        var patterns = SecretPatterns.Load(m365.SecretPatternsPath);
        return patterns.Patterns is null
            ? (null, 3, "configuration error: " + patterns.Error)
            : (new M365Session(memory.Paths!, memory.TimeZone!, m365, config, configuration.Warning, patterns.Patterns), 0, null);
    }

    /// <summary>The Graph objects of this session: the stub handler in tests, the real network otherwise.</summary>
    public GraphParts CreateGraph(M365VerbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var http = context.GraphHandler is { } handler
            ? new GraphHttp(handler, context.Clock, Patterns)
            : new GraphHttp(context.Clock, Patterns);
        var store = new CredentialFileSecretStore(Tenant, context.Environment, Instance.Paths.KeyFile, context.CheckKeyOwnership);
        var tokens = new GraphTokenClient(store, Tenant, Configuration, Instance.Paths.CertificateFile, http, context.Clock);
        return new GraphParts(http, tokens, new GraphReader(tokens, http, Configuration, context.Clock));
    }

    private static bool IsKnownTimeZone(M365VerbContext context, string id)
    {
        try
        {
            _ = context.FindTimeZone(id);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }
}

/// <summary>The HTTP exchange, the token client and the reader of one run; dispose to release the connection pool.</summary>
internal sealed record GraphParts(GraphHttp Http, GraphTokenClient Tokens, GraphReader Reader) : IDisposable
{
    public void Dispose() => Http.Dispose();
}
