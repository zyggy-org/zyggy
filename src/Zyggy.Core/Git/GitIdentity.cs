namespace Zyggy.Core.Git;

/// <summary>The committer identity a machine writes with (founding spec §5 "Git operations": <c>zyggy (&lt;machine&gt;) &lt;machine&gt;@zyggy.org</c>); passed per command, never written into the repository's config.</summary>
/// <param name="Name">The <c>user.name</c>.</param>
/// <param name="Email">The <c>user.email</c>.</param>
internal sealed record GitIdentity(string Name, string Email);
