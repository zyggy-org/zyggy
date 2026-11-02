namespace Zyggy.Core.Bus;

/// <summary>
/// How Central's pickup reaches OneDrive for <c>via: onedrive</c> items (spec 41, plan A13). Production leaves both at their defaults: the
/// real network and the key-file ownership check. Tests inject the stub Graph handler (the <see cref="GitHubBusOptions.HandlerFactory"/>
/// precedent).
/// </summary>
public sealed class ItemDriveOptions
{
    /// <summary>Gets or sets the factory of the Graph message handler; <see langword="null"/> is the real network.</summary>
    public Func<HttpMessageHandler>? GraphHandlerFactory { get; set; }

    /// <summary>Gets or sets a value indicating whether the Graph key file's owner and mode are checked (true in production).</summary>
    public bool CheckKeyOwnership { get; set; } = true;
}
