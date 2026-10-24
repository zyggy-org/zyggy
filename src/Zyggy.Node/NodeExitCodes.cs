namespace Zyggy.Node;

/// <summary>
/// The process exit codes of <c>zyggy-node</c> (spec 07, exit-code table): what a service manager or an operator sees when the
/// host ends. The runbook rows in the Core README say what each one means and what to do.
/// </summary>
public static class NodeExitCodes
{
    /// <summary>A normal stop (cancellation, service stop, SIGINT/SIGTERM).</summary>
    public const int Ok = 0;

    /// <summary>An unexpected exception ended the host (<c>BackgroundServiceExceptionBehavior.StopHost</c>); one <c>Error</c> line names the type.</summary>
    public const int Faulted = 1;

    /// <summary>A refused <c>node.json</c>, or one without <c>signWith</c>: one stderr line <c>zyggy-node: configuration error: &lt;key&gt;: &lt;detail&gt;</c>; nothing else started.</summary>
    public const int Configuration = 3;

    /// <summary>A start-up condition: the log directory cannot be created, or the bus checkout fails the §5 start-up check; one stderr/<c>Error</c> line names it.</summary>
    public const int Startup = 6;
}
