using System.Security.Cryptography;

using Zyggy.Core.Envelope;
using Zyggy.Core.Memory;

namespace Zyggy.Core.Bus;

/// <summary>What <see cref="IArchiveDropFolder.Probe"/> found at the laptop's OneDrive archive folder (plan 41 A6, spec 41 AC-21).</summary>
internal enum DropProbe
{
    /// <summary>The folder exists, is a directory and a probe file can be created and deleted in it.</summary>
    Available,

    /// <summary>Nothing exists at the configured path.</summary>
    Missing,

    /// <summary>The configured path is a file, not a directory.</summary>
    NotAFolder,

    /// <summary>The directory exists but a probe file cannot be created or deleted in it.</summary>
    AccessDenied,
}

/// <summary>The single mapping between <see cref="DropProbe"/> and the reason <c>zyggy archive</c> prints (spec 41 AC-21).</summary>
internal static class DropProbeWire
{
    /// <summary>Returns the reason string of a member.</summary>
    /// <param name="probe">The probe outcome.</param>
    /// <returns><c>available</c>, <c>missing</c>, <c>not_a_folder</c> or <c>access_denied</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for an undefined member.</exception>
    public static string ToWire(DropProbe probe) => probe switch
    {
        DropProbe.Available => "available",
        DropProbe.Missing => "missing",
        DropProbe.NotAFolder => "not_a_folder",
        DropProbe.AccessDenied => "access_denied",
        _ => throw new ArgumentOutOfRangeException(nameof(probe)),
    };
}

/// <summary>What <see cref="IArchiveDropFolder.Place"/> did with the item's bytes (plan 41 A6).</summary>
internal abstract record DropPlacement;

/// <summary>The file was created, flushed, re-read and its SHA-256 equals the bytes' (spec 41 AC-18).</summary>
internal sealed record DropWritten : DropPlacement;

/// <summary>A file with identical bytes was already there (a retried call, spec 41 AC-19); nothing was rewritten.</summary>
internal sealed record DropIdentical : DropPlacement;

/// <summary>A file with different bytes is already there (spec 41 AC-20); it was left untouched.</summary>
internal sealed record DropTaken : DropPlacement;

/// <summary>The write failed or read back different bytes (spec 41 AC-22); the file this call created, if any, was deleted.</summary>
/// <param name="Detail">What failed, without a byte of the item.</param>
internal sealed record DropFailed(string Detail) : DropPlacement;

/// <summary>
/// The laptop's locally synced OneDrive archive folder (<c>node.json</c> <c>archive.oneDriveFolder</c>, plan 41 A6): the laptop's own
/// sync client uploads what is placed here; Zyggy makes no cloud call, holds no credential and opens no connection on the laptop
/// (spec 41 decision 4). Not a §9 seam — there is no second backend in v1; tests substitute it through
/// <see cref="ItemSubmitter.DropFolderFactory"/>.
/// </summary>
internal interface IArchiveDropFolder
{
    /// <summary>Checks the folder: it exists, is a directory, and a zero-byte probe file can be created and deleted in it.</summary>
    /// <returns>The probe outcome.</returns>
    DropProbe Probe();

    /// <summary>
    /// Places the bytes at <see cref="TargetPath"/>: creates the project folder when absent, writes a new file (never overwrites), flushes,
    /// re-reads and compares SHA-256; identical bytes already present are reused, different bytes are left untouched; a failure after
    /// this call created the file deletes that file only.
    /// </summary>
    /// <param name="project">The project.</param>
    /// <param name="driveName">The item's file name, <c>&lt;slug&gt;.&lt;ext&gt;</c>.</param>
    /// <param name="bytes">The item's bytes.</param>
    /// <returns>The placement outcome.</returns>
    DropPlacement Place(Slug project, string driveName, ReadOnlySpan<byte> bytes);

    /// <summary>The one builder of the laptop's OneDrive target path: <c>&lt;root&gt;/&lt;project&gt;/&lt;driveName&gt;</c>.</summary>
    /// <param name="project">The project.</param>
    /// <param name="driveName">The item's file name.</param>
    /// <returns>The absolute target path.</returns>
    string TargetPath(Slug project, string driveName);
}

/// <summary>The file-system <see cref="IArchiveDropFolder"/> over the configured folder (plan 41 A6).</summary>
/// <param name="root">The absolute folder from <c>archive.oneDriveFolder</c>.</param>
/// <param name="clock">The clock naming the probe file (<c>.zyggy-probe-&lt;ULID&gt;</c>).</param>
internal sealed class FileSystemArchiveDropFolder(string root, TimeProvider clock) : IArchiveDropFolder
{
    private const string ReReadDiffers = "the re-read SHA-256 differs from the file's";

    /// <summary>Opens the new target file for writing — never over an existing file; one unit test substitutes a faulting stream.</summary>
    internal Func<string, Stream> OpenNew { get; init; } = path => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);

    /// <inheritdoc/>
    public DropProbe Probe()
    {
        if (File.Exists(root))
        {
            return DropProbe.NotAFolder;
        }

        if (!Directory.Exists(root))
        {
            return DropProbe.Missing;
        }

        string probe = Path.Join(root, ".zyggy-probe-" + EnvelopeId.New(clock.GetUtcNow()).Value);
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
            }

            File.Delete(probe);
            return DropProbe.Available;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return DropProbe.AccessDenied;
        }
    }

    /// <inheritdoc/>
    public DropPlacement Place(Slug project, string driveName, ReadOnlySpan<byte> bytes)
    {
        string target = TargetPath(project, driveName);
        byte[] expected = SHA256.HashData(bytes);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target))
            {
                // A retried call finds its own bytes (AC-19); anything else stays untouched (AC-20).
                return SHA256.HashData(File.ReadAllBytes(target)).AsSpan().SequenceEqual(expected) ? new DropIdentical() : new DropTaken();
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return new DropFailed(ex.Message);
        }

        Stream stream;
        try
        {
            stream = OpenNew(target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Not created by this call (CreateNew refuses an existing file): nothing to delete.
            return new DropFailed("write failed: " + ex.Message);
        }

        try
        {
            using (stream)
            {
                stream.Write(bytes);
                if (stream is FileStream file)
                {
                    file.Flush(flushToDisk: true);
                }
                else
                {
                    stream.Flush();
                }
            }

            if (SHA256.HashData(File.ReadAllBytes(target)).AsSpan().SequenceEqual(expected))
            {
                return new DropWritten();
            }

            DeleteOwn(target);
            return new DropFailed(ReReadDiffers);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            DeleteOwn(target);
            return new DropFailed("write failed: " + ex.Message);
        }
    }

    /// <inheritdoc/>
    public string TargetPath(Slug project, string driveName)
    {
        ArgumentNullException.ThrowIfNull(project);
        return Path.Join(root, project.Value, driveName);
    }

    // The file this call created, and only that one (AC-22); a failed delete leaves it for the owner, the outcome is already a failure.
    private static void DeleteOwn(string target)
    {
        try
        {
            File.Delete(target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
        }
    }
}
