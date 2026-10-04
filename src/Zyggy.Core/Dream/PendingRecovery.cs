using System.Text.Json;

using Zyggy.Core.Memory;

namespace Zyggy.Core.Dream;

/// <summary>What the next run must do about a dead run's pending marker.</summary>
/// <param name="Run">The dead run's id.</param>
/// <param name="Restore">Paths that still hold what the dead run wrote and existed before: restored from <c>HEAD</c>.</param>
/// <param name="Delete">Paths the dead run created and that still hold what it wrote: deleted.</param>
/// <param name="Dirty">A path someone edited since the dead run: nothing is touched and the run aborts with <c>dirty_pending</c>.</param>
internal sealed record PendingRecoveryResult(string Run, IReadOnlyList<string> Restore, IReadOnlyList<string> Delete, string? Dirty);

/// <summary>
/// AC-26: reads <c>.dream/pending.json</c> left by a run killed between write and commit and decides, path by path, whether the
/// dead run's content is still there (undo it), already gone (nothing to do) or replaced by someone else (dirty, touch nothing).
/// </summary>
internal static class PendingRecovery
{
    public static PendingRecoveryResult? Plan(MemoryPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (!File.Exists(paths.Pending))
        {
            return null;
        }

        string run;
        var restore = new List<string>();
        var delete = new List<string>();
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(paths.Pending));
            run = document.RootElement.GetProperty("run").GetString() ?? string.Empty;
            foreach (var file in document.RootElement.GetProperty("files").EnumerateArray())
            {
                var relative = file.GetProperty("path").GetString()!;
                var written = file.GetProperty("sha256_written").GetString();
                var before = file.GetProperty("sha256_before").GetString();
                var existedBefore = file.GetProperty("existed_before").GetBoolean();
                var full = Path.Join(paths.PrincipalDirectory, relative);
                var now = File.Exists(full) ? MemorySnapshot.Hash(File.ReadAllBytes(full)) : null;

                if (now == before && (now is not null || !existedBefore))
                {
                    continue; // already as it was before the dead run
                }

                if (now != written)
                {
                    return new PendingRecoveryResult(run, [], [], relative);
                }

                if (!existedBefore)
                {
                    delete.Add(relative);
                }
                else if (!relative.StartsWith("inbox/", StringComparison.Ordinal))
                {
                    restore.Add(relative); // an inbox file is never tracked; its deletion was planned and stays
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return new PendingRecoveryResult(string.Empty, [], [], ".dream/pending.json");
        }

        return new PendingRecoveryResult(run, restore, delete, null);
    }
}
