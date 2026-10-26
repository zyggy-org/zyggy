using Zyggy.Core.Secrets;

namespace Zyggy.Core.Configuration;

/// <summary>The <c>secrets</c> section of <c>node.json</c> (founding spec §8, §10 as amended by A9-3): which store and, for the file layer, where; never a secret itself.</summary>
/// <param name="Store">The store kind; <see cref="SecretStoreKind.Os"/> when the key is absent.</param>
/// <param name="Root">The file layer's root directory, or <see langword="null"/> for the per-OS default; ignored by the Credential Manager.</param>
public sealed record SecretsSettings(SecretStoreKind Store = SecretStoreKind.Os, string? Root = null);
