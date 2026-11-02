namespace Zyggy.Core.M365.Graph;

/// <summary>What kind of failure a Graph read was (the offload's reasons, spec 38; Central's pickup waits, spec 41).</summary>
internal enum GraphFailureKind
{
    /// <summary>403: the grant does not allow the read.</summary>
    Forbidden,

    /// <summary>429 or 503.</summary>
    Throttled,

    /// <summary>No answer: a network or TLS failure, a timeout.</summary>
    Transport,

    /// <summary>The token could not be minted or was refused (401 after a fresh token), or the key or configuration is unusable.</summary>
    Auth,

    /// <summary>Every other failure.</summary>
    Server,
}

/// <summary>Classifies <see cref="GraphReader"/>'s exit-6 failures by their message (the shell's wording) and exit code.</summary>
internal static class GraphFailureClass
{
    public static GraphFailureKind Classify(GraphFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        var message = failure.Message;
        return message.StartsWith("forbidden", StringComparison.Ordinal) ? GraphFailureKind.Forbidden
            : message.StartsWith("throttled", StringComparison.Ordinal) ? GraphFailureKind.Throttled
            : message.StartsWith("request failed", StringComparison.Ordinal) ? GraphFailureKind.Transport
            : message.StartsWith("unauthorized", StringComparison.Ordinal) || message.StartsWith("auth", StringComparison.Ordinal) || failure.ExitCode == 3
                ? GraphFailureKind.Auth
            : GraphFailureKind.Server;
    }
}
