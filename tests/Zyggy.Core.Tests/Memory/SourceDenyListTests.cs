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

    // Regression (Central, 2026-10-10): an image the owner sends in a session lands in ~/.claude/uploads/<session>/ and
    // was refused as denied_location; the uploads folder holds only what the owner handed over, so it is exempt.
    [Fact]
    public void Covers_SessionUpload_Null()
    {
        // Act & Assert
        Build().Covers(Path.Combine(Home, ".claude", "uploads", "e80198bf-5065-45d8-8599-a53a50e5caa6", "c5082833-image.png")).Should().BeNull();
    }

    public static TheoryData<string> ClaudeOutsideUploads() => new()
    {
        Path.Combine(Home, ".claude", ".credentials.json"),
        Path.Combine(Home, ".claude", "projects", "x.jsonl"),
        Path.Combine(Home, ".claude", "uploadsx", "x.png"),
        Path.Combine(Home, ".claude", "uploads", "..", ".credentials.json"),
        Path.Combine(Home, ".claude", "uploads"),
    };

    [Theory]
    [MemberData(nameof(ClaudeOutsideUploads))]
    public void Covers_ClaudeOutsideUploads_StillDenied(string path)
    {
        // Act & Assert
        Build().Covers(path).Should().Be("denied_location");
    }

    [Fact]
    public void Covers_UploadUnderInstanceSourceDeny_StillDenied()
    {
        // Arrange: the instance may still close the uploads folder (source_deny only tightens).
        var uploads = Path.Combine(Home, ".claude", "uploads");

        // Act & Assert
        Build(new ArchiveOptions { SourceDeny = [uploads] }).Covers(Path.Combine(uploads, "s", "x.png")).Should().Be("denied_location");
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
