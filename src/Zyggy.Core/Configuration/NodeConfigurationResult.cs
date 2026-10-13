namespace Zyggy.Core.Configuration;

/// <summary>The outcome of reading <c>node.json</c>: the typed configuration, or the first refusal with the key at fault.</summary>
public sealed class NodeConfigurationResult
{
    private NodeConfigurationResult(NodeConfiguration? configuration, NodeConfigurationError? error, string? field, string? detail)
    {
        Configuration = configuration;
        Error = error;
        Field = field;
        Detail = detail ?? string.Empty;
    }

    /// <summary>Gets the configuration when the file was accepted.</summary>
    public NodeConfiguration? Configuration { get; }

    /// <summary>Gets the refusal when the file was not accepted.</summary>
    public NodeConfigurationError? Error { get; }

    /// <summary>Gets the key at fault, when one is.</summary>
    public string? Field { get; }

    /// <summary>Gets a short detail of the refusal; empty on success.</summary>
    public string Detail { get; }

    /// <summary>Gets a value indicating whether the file was accepted.</summary>
    public bool IsOk => Configuration is not null;

    /// <summary>An accepted file.</summary>
    /// <param name="configuration">The typed configuration.</param>
    /// <returns>The result.</returns>
    public static NodeConfigurationResult Ok(NodeConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new NodeConfigurationResult(configuration, null, null, null);
    }

    /// <summary>A refused file.</summary>
    /// <param name="error">The refusal.</param>
    /// <param name="field">The key at fault, when one is.</param>
    /// <param name="detail">A short detail.</param>
    /// <returns>The result.</returns>
    public static NodeConfigurationResult Fail(NodeConfigurationError error, string? field, string detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        return new NodeConfigurationResult(null, error, field, detail);
    }

    /// <summary>The one-line message a verb prints: <c>&lt;key&gt;: &lt;detail&gt;</c> or just the detail.</summary>
    /// <returns>The message.</returns>
    public override string ToString() => IsOk ? "ok" : Field is null ? Detail : Field + ": " + Detail;
}
