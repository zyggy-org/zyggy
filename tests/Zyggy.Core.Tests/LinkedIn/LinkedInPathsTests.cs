using Zyggy.Core.LinkedIn;

namespace Zyggy.Core.Tests.LinkedIn;

/// <summary>The LinkedIn paths (spec 36 Files): the state and credential roots and their file names.</summary>
public sealed class LinkedInPathsTests
{
    [Fact]
    public void Paths_FromStateDirAndXdgConfigHome()
    {
        // Act
        var paths = new LinkedInPaths(new Dictionary<string, string?>
        {
            ["HOME"] = "/home/z",
            ["ZYGGY_STATE_DIR"] = "/var/s",
            ["XDG_CONFIG_HOME"] = "/etc/c",
        });

        // Assert
        paths.StateDirectory.Should().Be(Path.Join("/var/s", "linkedin"));
        paths.ActionLog.Should().Be(Path.Join("/var/s", "linkedin", "actions.jsonl"));
        paths.PendingAuth.Should().Be(Path.Join("/var/s", "linkedin", "pending-auth.json"));
        paths.ConfigDirectory.Should().Be(Path.Join("/etc/c", "zyggy", "linkedin"));
        paths.TokenFile.Should().Be(Path.Join("/etc/c", "zyggy", "linkedin", "token.json"));
        paths.ClientSecretFile.Should().Be(Path.Join("/etc/c", "zyggy", "linkedin", "client-secret"));
    }

    [Fact]
    public void Paths_DefaultsUnderHome()
    {
        // Act
        var paths = new LinkedInPaths(new Dictionary<string, string?> { ["HOME"] = "/home/z", ["ZYGGY_STATE_DIR"] = string.Empty });

        // Assert
        paths.StateDirectory.Should().Be(Path.Join("/home/z", ".local", "state", "zyggy", "linkedin"));
        paths.ConfigDirectory.Should().Be(Path.Join("/home/z", ".config", "zyggy", "linkedin"));
    }

    [Fact]
    public void CredentialsDirectorySecret_NullWithoutCredentialsDirectory()
    {
        // Act
        var paths = new LinkedInPaths(new Dictionary<string, string?> { ["HOME"] = "/home/z" });

        // Assert
        paths.CredentialsDirectorySecret.Should().BeNull();
    }

    [Fact]
    public void CredentialsDirectorySecret_UnderCredentialsDirectory()
    {
        // Act
        var paths = new LinkedInPaths(new Dictionary<string, string?> { ["HOME"] = "/home/z", ["CREDENTIALS_DIRECTORY"] = "/run/cred/u" });

        // Assert
        paths.CredentialsDirectorySecret.Should().Be(Path.Join("/run/cred/u", "linkedin-client-secret"));
    }
}
