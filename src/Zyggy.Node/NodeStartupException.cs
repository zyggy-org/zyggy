namespace Zyggy.Node;

/// <summary>
/// Thrown by <see cref="NodeHost.Build"/> when the host cannot be built: a refused <c>node.json</c> or a missing <c>signWith</c>
/// (<see cref="NodeExitCodes.Configuration"/>), or a log directory that cannot be created (<see cref="NodeExitCodes.Startup"/>).
/// <see cref="NodeHost.RunAsync"/> turns it into the one stderr line and the exit code (plan 07 A12).
/// </summary>
public sealed class NodeStartupException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="NodeStartupException"/> class.</summary>
    public NodeStartupException()
        : this(NodeExitCodes.Startup, "the node could not start")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="NodeStartupException"/> class with a message.</summary>
    /// <param name="message">What stopped the start.</param>
    public NodeStartupException(string message)
        : this(NodeExitCodes.Startup, message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="NodeStartupException"/> class with a message and a cause.</summary>
    /// <param name="message">What stopped the start.</param>
    /// <param name="innerException">The cause.</param>
    public NodeStartupException(string message, Exception innerException)
        : base(message, innerException)
    {
        ExitCode = NodeExitCodes.Startup;
    }

    /// <summary>Initializes a new instance of the <see cref="NodeStartupException"/> class with an exit code and a message.</summary>
    /// <param name="exitCode">The process exit code (<see cref="NodeExitCodes"/>).</param>
    /// <param name="message">What stopped the start — for <see cref="NodeExitCodes.Configuration"/> the <c>&lt;key&gt;: &lt;detail&gt;</c> text of the reader.</param>
    public NodeStartupException(int exitCode, string message)
        : base(message)
    {
        ExitCode = exitCode;
    }

    /// <summary>Gets the process exit code this failure ends in.</summary>
    public int ExitCode { get; }
}
