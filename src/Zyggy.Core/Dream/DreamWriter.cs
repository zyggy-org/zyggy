using System.Text;
using System.Text.Json;

using Zyggy.Core.Memory;

namespace Zyggy.Core.Dream;

/// <summary>
/// Writes an accepted working set to disk (spec 28 AC-14, AC-26): first <c>.dream/pending.json</c> listing every path with the hash
/// it will hold and the hash it held, then each file atomically, then the ledger. The marker is deleted after the commit; a run
/// killed in between leaves it for the next run's recovery.
/// </summary>
/// <param name="onWrite">Test hook called with each relative path right after it is written.</param>
internal sealed class DreamWriter(Action<string>? onWrite = null)
{
    private const string Ledger = ".dream/ledger.json";
    private const string Pending = ".dream/pending.json";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public IReadOnlyList<string> Write(MemoryPaths paths, WorkingSet set, string runId)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(set);
        var changed = set.ChangedPaths.Where(p => p != Pending).ToList();
        var ordered = changed.Where(p => p != Ledger).Concat(changed.Where(p => p == Ledger)).ToList();

        MemoryFileWriter.WriteAtomically(paths.Pending, Marker(set, ordered, runId));
        onWrite?.Invoke(Pending);
        foreach (var relative in ordered)
        {
            var full = Path.Join(paths.PrincipalDirectory, relative);
            if (set.Text(relative) is { } text)
            {
                MemoryFileWriter.WriteAtomically(full, text);
            }
            else if (File.Exists(full))
            {
                File.Delete(full);
            }

            onWrite?.Invoke(relative);
        }

        return ordered;
    }

    public static void DeleteMarker(MemoryPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        File.Delete(paths.Pending);
    }

    private static string Marker(WorkingSet set, IReadOnlyList<string> paths, string runId)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            writer.WriteStartObject();
            writer.WriteString("run", runId);
            writer.WriteStartArray("files");
            foreach (var path in paths)
            {
                writer.WriteStartObject();
                writer.WriteString("path", path);
                if (set.Text(path) is { } text)
                {
                    writer.WriteString("sha256_written", MemorySnapshot.Hash(Utf8NoBom.GetBytes(text)));
                }
                else
                {
                    writer.WriteNull("sha256_written");
                }

                var before = set.Snapshot.Files.GetValueOrDefault(path);
                writer.WriteBoolean("existed_before", before is not null);
                if (before is not null)
                {
                    writer.WriteString("sha256_before", before.Sha256);
                }
                else
                {
                    writer.WriteNull("sha256_before");
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Utf8NoBom.GetString(buffer.ToArray()) + "\n";
    }
}
