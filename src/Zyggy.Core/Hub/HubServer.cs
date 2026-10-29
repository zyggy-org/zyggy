using System.Reflection;

using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using Zyggy.Core.Memory;

namespace Zyggy.Core.Hub;

/// <summary>
/// The Hub over stdio with the official SDK and no generic host (spec 11, D5; the shape of 36's LinkedIn server): server name
/// <c>hub</c> (so the permission rules are <c>mcp__hub__get_context</c> and <c>mcp__hub__remember</c>), the assembly's version, the
/// tools capability. Nothing but protocol lines reaches stdout; the SDK logs nowhere; refusals of a named principal go to
/// <c>diagnostics</c>, one line each. Returns 0 when stdin closes.
/// </summary>
internal static class HubServer
{
    public const string ServerName = "hub";

    /// <exception cref="ArgumentException"><paramref name="start"/> carries no principal, time zone or secret patterns.</exception>
    public static async Task<int> RunAsync(
        Stream stdin,
        Stream stdout,
        TextWriter diagnostics,
        HubStart start,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stdin);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(clock);
        if (start.Paths is null || start.TimeZone is null || start.Patterns is null)
        {
            throw new ArgumentException("The Hub starts only with a principal, a time zone and the secret patterns.", nameof(start));
        }

        var tools = new McpServerPrimitiveCollection<McpServerTool>(StringComparer.Ordinal)
        {
            new GetContextTool(new MemoryStore(start.Paths), new ContextOptions(), start.Paths, start.TimeZone, clock, diagnostics),
            new RememberTool(new RememberService(start.Paths, start.TimeZone, clock, start.Patterns), start.HooksOff, diagnostics),
        };
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = ServerName, Version = Version() },
            Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
            ToolCollection = tools,
        };

        await using var transport = new StreamServerTransport(stdin, stdout, ServerName, NullLoggerFactory.Instance);
        await using var server = McpServer.Create(transport, options, NullLoggerFactory.Instance, serviceProvider: null);
        await server.RunAsync(cancellationToken).ConfigureAwait(false);
        return 0;
    }

    private static string Version() =>
        typeof(HubServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
}
