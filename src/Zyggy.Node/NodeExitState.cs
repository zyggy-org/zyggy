namespace Zyggy.Node;

/// <summary>The exit code the loop decides on before it calls <c>StopApplication()</c>; <see cref="NodeHost.RunAsync"/> returns it. <c>0</c> until set.</summary>
internal sealed class NodeExitState
{
    /// <summary>Gets or sets the process exit code (<see cref="NodeExitCodes"/>).</summary>
    public int Code { get; set; }
}
