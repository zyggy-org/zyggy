using System.Reflection;

using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Zyggy.Core.LinkedIn.Mcp;

/// <summary>
/// The <c>linkedin</c> MCP server over stdio with the official SDK and no generic host (spec 36 AC-1): server name <c>linkedin</c> (so the
/// permission rule is <c>mcp__linkedin__publish_post</c>), the assembly's version, the tools capability, and <c>publish_post</c> only
/// when publishing is switched on. Nothing but protocol lines reaches stdout; the SDK logs nowhere. Returns 0 when stdin closes.
/// </summary>
internal static class LinkedInMcpServer
{
    public const string ServerName = "linkedin";

    public static async Task<int> RunAsync(Stream stdin, Stream stdout, ServerStart start, Func<IPublishTool> createTool, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(createTool);
        var tools = new McpServerPrimitiveCollection<McpServerTool>(StringComparer.Ordinal);
        using var tool = start.OfferTool ? createTool() : null;
        if (tool is not null)
        {
            tools.Add(new PublishPostMcpTool(tool, start.Config!.PostMaxChars));
        }

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
        typeof(LinkedInMcpServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
}
