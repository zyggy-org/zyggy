using System.Runtime.Versioning;
using System.Text;

using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Bus;
using Zyggy.Core.Memory;

namespace Zyggy.Core.Tests.Bus;

/// <summary>
/// Plan 41 A6, spec 41 AC-18..AC-22 on a real temp folder standing in for the laptop's synced OneDrive archive folder: the probe, a new
/// file placed and re-read, identical bytes reused, different bytes left alone, and a failed or mismatching write that deletes only the
/// file this call created.
/// </summary>
public sealed class FileSystemArchiveDropFolderTests : IDisposable
{
    private const string DriveName = "staging-restore-notes.txt";

    private static readonly Slug Calizr = Slug.Parse("calizr");

    private static readonly byte[] Notes = Encoding.UTF8.GetBytes("# Staging restore\n\nRestore the staging database from Sunday's snapshot.\n");

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 31, 14, 5, 0, TimeSpan.Zero));

    private readonly string _root = Path.Combine(Path.GetTempPath(), "zyggy-ut-drop", Guid.NewGuid().ToString("N"));

    public static bool IsLinux => OperatingSystem.IsLinux();

    private string Folder => Path.Combine(_root, "OneDrive", "Zyggy", "archive");

    private string Target => Path.Combine(Folder, "calizr", DriveName);

    public void Dispose()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            foreach (string directory in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories))
            {
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("missing", "Missing")]
    [InlineData("file", "NotAFolder")]
    [InlineData("folder", "Available")]
    public void Probe_Missing_NotAFolder_Available(string arrange, string expected)
    {
        // Arrange
        Directory.CreateDirectory(Path.GetDirectoryName(Folder)!);
        _ = arrange switch
        {
            "file" => WriteFile(Folder, [1]),
            "folder" => Directory.CreateDirectory(Folder).FullName,
            _ => string.Empty,
        };

        // Act
        DropProbe probe = new FileSystemArchiveDropFolder(Folder, _clock).Probe();

        // Assert
        probe.Should().Be(Enum.Parse<DropProbe>(expected));
        (Directory.Exists(Folder) ? Directory.EnumerateFileSystemEntries(Folder) : []).Should().BeEmpty("the probe file is deleted again");
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "Unix file modes: Linux only")]
    [SupportedOSPlatform("linux")]
    public void Probe_ReadOnlyDirectory_AccessDenied()
    {
        // Arrange
        Directory.CreateDirectory(Folder);
        File.SetUnixFileMode(Folder, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        // Act
        DropProbe probe = new FileSystemArchiveDropFolder(Folder, _clock).Probe();

        // Assert
        probe.Should().Be(DropProbe.AccessDenied);
    }

    [Fact]
    public void Place_NewFile_ProjectFolderCreated_BytesEqual_DropWritten()
    {
        // Arrange
        Directory.CreateDirectory(Folder);
        var drop = new FileSystemArchiveDropFolder(Folder, _clock);

        // Act
        DropPlacement placement = drop.Place(Calizr, DriveName, Notes);

        // Assert
        placement.Should().Be(new DropWritten());
        drop.TargetPath(Calizr, DriveName).Should().Be(Path.Join(Folder, "calizr", DriveName));
        File.ReadAllBytes(Target).Should().Equal(Notes);
        Directory.EnumerateFileSystemEntries(Path.Combine(Folder, "calizr")).Should().ContainSingle();
    }

    [Fact]
    public void Place_SameBytesPresent_DropIdentical_FileUntouched()
    {
        // Arrange
        Directory.CreateDirectory(Folder);
        WriteFile(Target, Notes);
        var written = new DateTime(2026, 10, 30, 9, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Target, written);

        // Act
        DropPlacement placement = new FileSystemArchiveDropFolder(Folder, _clock).Place(Calizr, DriveName, Notes);

        // Assert
        placement.Should().Be(new DropIdentical());
        File.GetLastWriteTimeUtc(Target).Should().Be(written);
        File.ReadAllBytes(Target).Should().Equal(Notes);
    }

    [Fact]
    public void Place_OtherBytesPresent_DropTaken_FileUntouched()
    {
        // Arrange
        Directory.CreateDirectory(Folder);
        byte[] other = Encoding.UTF8.GetBytes("# Another note\n");
        WriteFile(Target, other);
        var written = new DateTime(2026, 10, 30, 9, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Target, written);

        // Act
        DropPlacement placement = new FileSystemArchiveDropFolder(Folder, _clock).Place(Calizr, DriveName, Notes);

        // Assert
        placement.Should().Be(new DropTaken());
        File.ReadAllBytes(Target).Should().Equal(other);
        File.GetLastWriteTimeUtc(Target).Should().Be(written);
    }

    [Fact]
    public void Place_WriteThrowsMidway_DeletesOnlyItsOwnFile_DropFailed()
    {
        // Arrange
        string sibling = WriteFile(Path.Combine(Folder, "calizr", "older-notes.txt"), [1, 2, 3]);
        var drop = new FileSystemArchiveDropFolder(Folder, _clock) { OpenNew = path => new FaultingStream(OpenCreateNew(path), failAfter: 10, corrupt: false) };

        // Act
        DropPlacement placement = drop.Place(Calizr, DriveName, Notes);

        // Assert
        placement.Should().BeOfType<DropFailed>().Which.Detail.Should().StartWith("write failed: ");
        File.Exists(Target).Should().BeFalse();
        File.ReadAllBytes(sibling).Should().Equal(1, 2, 3);
    }

    [Fact]
    public void Place_ReReadDiffers_DeletesItsOwnFile_DropFailed()
    {
        // Arrange
        Directory.CreateDirectory(Folder);
        var drop = new FileSystemArchiveDropFolder(Folder, _clock) { OpenNew = path => new FaultingStream(OpenCreateNew(path), failAfter: int.MaxValue, corrupt: true) };

        // Act
        DropPlacement placement = drop.Place(Calizr, DriveName, Notes);

        // Assert
        placement.Should().Be(new DropFailed("the re-read SHA-256 differs from the file's"));
        File.Exists(Target).Should().BeFalse();
    }

    private static FileStream OpenCreateNew(string path) => new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);

    private static string WriteFile(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>Wraps the real file stream: throws after <c>failAfter</c> bytes, or flips the first byte it writes.</summary>
    private sealed class FaultingStream(Stream inner, int failAfter, bool corrupt) : Stream
    {
        private int _written;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => inner.Length;

        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (_written + buffer.Length > failAfter)
            {
                inner.Write(buffer[..(failAfter - _written)]);
                throw new IOException("There is not enough space on the disk.");
            }

            byte[] copy = buffer.ToArray();
            if (corrupt && _written == 0 && copy.Length > 0)
            {
                copy[0] ^= 0xFF;
            }

            inner.Write(copy);
            _written += copy.Length;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
