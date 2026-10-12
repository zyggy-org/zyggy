namespace Zyggy.Core.Configuration;

/// <summary>The <c>secrets</c> section of <c>node.json</c> (founding spec §8, P0 file store): where the file secret store lives; never a secret itself.</summary>
/// <param name="Root">The store's root directory, or <see langword="null"/> for the per-OS default.</param>
public sealed record SecretsSettings(string? Root = null);
