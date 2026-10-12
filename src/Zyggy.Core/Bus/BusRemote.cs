namespace Zyggy.Core.Bus;

/// <summary>Describes the bus repository on its host (founding spec §5 "Credentials per machine"): a description only, never a credential.</summary>
/// <param name="Owner">The host account or organisation that owns the repository.</param>
/// <param name="Repository">The repository name.</param>
/// <param name="Branch">The branch the bus lives on (<c>main</c>).</param>
/// <param name="HttpsUrl">The HTTPS clone URL, without user information.</param>
/// <param name="SshUrl">The SSH clone URL, when the machine uses a deploy key; otherwise <see langword="null"/>.</param>
public sealed record BusRemote(string Owner, string Repository, string Branch, Uri HttpsUrl, Uri? SshUrl);
