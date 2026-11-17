using System.Text;

using Microsoft.Extensions.Logging;

using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Memory.Mirror;

/// <summary>
/// Builds the snapshot one machine may read from a principal's memory working tree (spec 47 "What each machine may read"): the files
/// its reach covers, never <c>inbox/</c>, <c>auto/</c>, <c>.dream/</c>, a symbolic link or an archived item file (under
/// <c>archive/</c> only sidecars), each file withheld whole when a line hits a secret or contact-detail pattern — its path logged once,
/// never the line — plus <c>MIRROR.md</c>. Reads files only; no process, no network.
/// </summary>
internal sealed partial class MirrorSnapshotBuilder
{
    private readonly SecretPatterns _secrets;

    private readonly ILogger<MirrorSnapshotBuilder> _logger;

    public MirrorSnapshotBuilder(SecretPatterns secrets, ILogger<MirrorSnapshotBuilder> logger)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(logger);
        _secrets = secrets;
        _logger = logger;
    }

    /// <summary>Builds the snapshot of <paramref name="memory"/> for <paramref name="machine"/>.</summary>
    /// <param name="memory">The principal's memory paths (the working tree at the source revision).</param>
    /// <param name="machine">The machine the mirror serves (for the log).</param>
    /// <param name="reach">What it may read.</param>
    /// <param name="source">The revision and date <c>MIRROR.md</c> names.</param>
    /// <returns>The snapshot.</returns>
    public MirrorSnapshot Build(MemoryPaths memory, MachineName machine, MirrorReach reach, MirrorSource source)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(reach);
        ArgumentNullException.ThrowIfNull(source);
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [MirrorPaths.ReadMe] = new UTF8Encoding(false).GetBytes(MirrorReadMe.Render(source)),
        };
        var withheld = new List<string>();
        var root = new DirectoryInfo(memory.PrincipalDirectory);
        if (root.Exists)
        {
            foreach (var file in RegularFiles(root))
            {
                var relative = memory.Relative(file.FullName);
                if (!Copies(memory, relative, reach))
                {
                    continue;
                }

                var bytes = File.ReadAllBytes(file.FullName);
                var mirrorPath = MirrorPaths.File(memory.Principal, relative);
                if (Hit(bytes) is { } kind)
                {
                    withheld.Add(mirrorPath);
                    LogWithheld(machine.Value, mirrorPath, kind);
                    continue;
                }

                files[mirrorPath] = bytes;
            }
        }

        return new MirrorSnapshot(files, [.. withheld.Order(StringComparer.Ordinal)]);
    }

    // Files under the directory, never through a symbolic link: a linked file or folder is skipped, not followed.
    private static IEnumerable<FileInfo> RegularFiles(DirectoryInfo directory)
    {
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            if (entry.LinkTarget is not null)
            {
                continue;
            }

            if (entry is DirectoryInfo sub)
            {
                foreach (var file in RegularFiles(sub))
                {
                    yield return file;
                }
            }
            else if (entry is FileInfo file)
            {
                yield return file;
            }
        }
    }

    private static bool Copies(MemoryPaths memory, string relative, MirrorReach reach)
    {
        if (!reach.Includes(relative) || MirrorPaths.NeverCopied.Any(area => MirrorReach.Covers(area, relative)))
        {
            return false;
        }

        if (!MirrorReach.Covers(MirrorPaths.Archive, relative))
        {
            return true;
        }

        var resolution = memory.TryResolve(relative);
        return resolution is { Succeeded: true, Area: MemoryArea.ArchiveSidecar }
            && ArchiveSidecar.TryParse(MemoryFileReader.Read(resolution.FullPath!)) is not null;
    }

    // The kind of the first hit (a secret pattern's name or "contact detail"), never the line itself.
    private string? Hit(byte[] bytes)
    {
        var lines = TextLines.Split(Encoding.UTF8.GetString(bytes));
        if (_secrets.TryMatchLines(lines, out var name, out _))
        {
            return "secret pattern " + name;
        }

        return lines.Any(ContactDetailPatterns.Contains) ? "contact detail" : null;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "mirror {Machine}: withheld {Path} ({Kind})")]
    private partial void LogWithheld(string machine, string path, string kind);
}
