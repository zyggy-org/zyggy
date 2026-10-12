namespace Zyggy.Core.Bus;

/// <summary>
/// Settings of the GitHub bus provider registered by <see cref="ServiceCollectionExtensions.AddGitHubBus"/>. There is no
/// hidden default: <see cref="Remote"/>, <see cref="Identity"/> and <see cref="CheckoutRoot"/> must be set (from
/// <c>node.json</c>) or resolving the provider fails. Never a credential: the token comes from <c>ISecretStore</c> on every request.
/// </summary>
public sealed class GitHubBusOptions
{
    /// <summary>Gets or sets the bus repository on GitHub.</summary>
    public BusRemote? Remote { get; set; }

    /// <summary>Gets or sets this node's tenant and machine name.</summary>
    public BusIdentity? Identity { get; set; }

    /// <summary>Gets or sets the local checkout of the bus repository.</summary>
    public string? CheckoutRoot { get; set; }

    /// <summary>Gets or sets the product version sent as <c>User-Agent: zyggy/&lt;version&gt;</c>; the assembly's informational version when null.</summary>
    public string? Version { get; set; }

    /// <summary>
    /// Gets or sets the API base; <see langword="null"/> means GitHub's public API. Only tests set it (spec 04 Assumption A3):
    /// it points the §5 request at a loopback stand-in, never a credential.
    /// </summary>
    public Uri? ApiBaseUri { get; set; }

    /// <summary>A test-only replacement for the HTTP handler (spec 04 Step 5): the integration harness answers the head check from a bare repository.</summary>
    internal Func<HttpMessageHandler>? HandlerFactory { get; set; }
}
