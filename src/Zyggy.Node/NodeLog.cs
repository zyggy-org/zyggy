namespace Zyggy.Node;

/// <summary>The host's log lines (§11: once per state change; every line carries <c>tenant</c> and <c>machine</c> through the Serilog enrichers).</summary>
internal static partial class NodeLog
{
    [LoggerMessage(EventId = 7001, Level = LogLevel.Information, Message = "node started: tenant {Tenant}, machine {Machine}, version {Version}")]
    public static partial void NodeStarted(ILogger logger, string tenant, string machine, string version);
}
