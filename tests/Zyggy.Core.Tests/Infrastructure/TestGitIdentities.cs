using Zyggy.Core.Git;

namespace Zyggy.Core.Tests.Infrastructure;

/// <summary>The commit identities the unit tests give a machine's git client: the product's placeholder <c>zyggy (&lt;machine&gt;)</c> form.</summary>
internal static class TestGitIdentities
{
    public static GitIdentity HomeLaptop { get; } = new("zyggy (home-laptop)", "home-laptop@zyggy.org");

    public static GitIdentity Central { get; } = new("zyggy (central)", "central@zyggy.org");
}
