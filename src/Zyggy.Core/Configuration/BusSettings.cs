namespace Zyggy.Core.Configuration;

/// <summary>
/// The <c>bus</c> section of <c>node.json</c> (founding spec §10): raw strings only — the host-specific URLs are derived by
/// the bus provider, the one type that knows the host (§9).
/// </summary>
/// <param name="Owner">The host account or organisation owning the bus repository.</param>
/// <param name="Repo">The repository name.</param>
/// <param name="Branch">The bus branch; <c>main</c> when absent.</param>
/// <param name="LocalPath">The absolute path of the local checkout.</param>
/// <param name="Remote">The checkout's expected <c>origin</c> URL (HTTPS or SSH form).</param>
public sealed record BusSettings(string Owner, string Repo, string Branch, string LocalPath, string Remote);
