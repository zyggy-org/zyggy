using Zyggy.Core.Memory;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Memory;

public sealed class SourceDenyListTests : IDisposable
{
    private static readonly string Home = Path.Combine(Path.GetTempPath(), "zyggy-ut-deny", "home");
    private static readonly string Xdg = Path.Combine(Path.GetTempPath(), "zyggy-ut-deny", "xdg");
    private static readonly string Credentials = Path.Combine(Path.GetTempPath(), "zyggy-ut-deny", "creds");

    private readonly MemoryTree _tree = new();

    public static TheoryData<string> BuiltIn() => new()
    {
        Path.Combine(Home, ".ssh", "id_ed25519"),
        Path.Combine(Home, ".config", "zyggy", "x"),
        Path.Combine(Xdg, "zyggy", "x"),
        Path.Combine(Credentials, "x"),
        Path.Combine(Home, ".local", "state", "zyggy", "x"),
        Path.Combine(Home, ".claude", "x"),
    };

    public void Dispose() => _tree.Dispose();

    [Theory]
    [MemberData(nameof(BuiltIn))]
    public void Covers_BuiltInLocations_DeniedLocation(string path)
    {
        // Act
        var covered = Build().Covers(path);

        // Assert
        covered.Should().Be("denied_location");
    }

    [Fact]
    public void Covers_InsideMemoryRoot_InsideMemory()
    {
        // Act
        var covered = Build().Covers(_tree.Full("archive/zyggy/quote.pdf"));

        // Assert
        covered.Should().Be("inside_memory");
    }

    [Fact]
    public void Covers_InstanceSourceDeny_DeniedLocation()
    {
        // Arrange
        var secretDocs = Path.Combine(Home, "Documents", "secret");

        // Act
        var covered = Build(new ArchiveOptions { SourceDeny = [secretDocs] }).Covers(Path.Combine(secretDocs, "x.pdf"));

        // Assert
        covered.Should().Be("denied_location");
    }

    [Fact]
    public void Covers_StagingFolder_Null()
    {
        // Act & Assert
        Build().Covers(Path.Combine(Home, ".cache", "zyggy", "archive-staging", "x.txt")).Should().BeNull();
    }

    [Fact]
    public void Covers_LinkedInMediaFolder_Null()
    {
        // Act & Assert
        Build().Covers(Path.Combine(Home, ".local", "share", "zyggy", "linkedin", "media", "x.png")).Should().BeNull();
    }

    [Fact]
    public void Covers_PrefixLookalike_Null()
    {
        // Act & Assert
        Build().Covers(Path.Combine(Home, ".sshx", "x")).Should().BeNull();
    }

    private SourceDenyList Build(ArchiveOptions? options = null)
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["HOME"] = Home,
            ["XDG_CONFIG_HOME"] = Xdg,
            ["CREDENTIALS_DIRECTORY"] = Credentials,
        };
        return SourceDenyList.Build(env, options ?? new ArchiveOptions(), _tree.Paths);
    }
}
