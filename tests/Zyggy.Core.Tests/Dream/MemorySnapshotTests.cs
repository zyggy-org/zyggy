using Zyggy.Core.Dream;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Dream;

/// <summary>
/// Spec 37 AC-21: the dream's snapshot lists an archived item by path and size and never reads its bytes; sidecars stay ordinary files;
/// every other file is read and hashed as before.
/// </summary>
public sealed class MemorySnapshotTests : IDisposable
{
    private const string Sidecar =
        "---\nname: Quote\ndescription: Roof quote\nupdated: 2026-09-30\nproject: zyggy\nmedia_type: application/pdf\nsize_bytes: 3\n"
        + "sha256: abc\narchived: 2026-09-30\nsource_name: q.pdf\n---\nRoof quote\n";

    private readonly MemoryTree _tree = new(
        ("profile.md", "---\nname: profile\ndescription: who Alice is\nupdated: 2026-09-18\n---\n- [stated] 2026-09-18: Alice.\n"),
        ("archive/zyggy/q.pdf", "%PD"),
        ("archive/zyggy/q.md", Sidecar));

    public void Dispose() => _tree.Dispose();

    [Fact]
    public void Load_ArchiveItem_NotInFilesListedWithSize()
    {
        // Act
        var snapshot = MemorySnapshot.Load(_tree.Paths);

        // Assert
        snapshot.ArchiveItems.Should().Equal(new Dictionary<string, long> { ["archive/zyggy/q.pdf"] = 3 });
        snapshot.Files.Should().NotContainKey("archive/zyggy/q.pdf");
    }

    [Fact]
    public void Load_ArchiveSidecar_InFilesParsed()
    {
        // Act
        var snapshot = MemorySnapshot.Load(_tree.Paths);

        // Assert
        var sidecar = snapshot.Files["archive/zyggy/q.md"];
        sidecar.Parsed!.Name.Should().Be("Quote");
        sidecar.Parsed.UnknownKeys["media_type"].Should().Be("application/pdf");
    }

    [Fact]
    public void Load_ItemBytesNeverRead()
    {
        // Arrange: the item cannot be opened for reading while the snapshot loads.
        var item = _tree.Full("archive/zyggy/q.pdf");
        using var locked = OperatingSystem.IsWindows() ? new FileStream(item, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(item, UnixFileMode.None);
        }

        try
        {
            // Act
            var snapshot = MemorySnapshot.Load(_tree.Paths);

            // Assert
            snapshot.ArchiveItems.Should().ContainKey("archive/zyggy/q.pdf");
        }
        finally
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(item, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
    }

    [Fact]
    public void Load_OtherAreasUnchanged()
    {
        // Act
        var snapshot = MemorySnapshot.Load(_tree.Paths);

        // Assert
        snapshot.Files.Keys.Should().BeEquivalentTo("profile.md", "archive/zyggy/q.md");
        snapshot.Files["profile.md"].Sha256.Should().Be(MemorySnapshot.Hash(File.ReadAllBytes(_tree.Full("profile.md"))));
    }
}
