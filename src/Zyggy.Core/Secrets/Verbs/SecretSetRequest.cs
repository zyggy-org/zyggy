namespace Zyggy.Core.Secrets.Verbs;

/// <summary>What <c>zyggy secret set</c> was asked: the name, whether the input is hex, whether to generate a key, whether to show it once.</summary>
/// <param name="Name">The tenant-relative name.</param>
/// <param name="Hex">The input is lowercase or uppercase hex, not raw bytes.</param>
/// <param name="Generate">Generate 32 random bytes instead of reading a value (<c>hmac/&lt;n&gt;</c> names only).</param>
/// <param name="Show">Print the generated value once, as hex, to carry it to the tenant's other machines.</param>
public sealed record SecretSetRequest(SecretName Name, bool Hex, bool Generate, bool Show);
