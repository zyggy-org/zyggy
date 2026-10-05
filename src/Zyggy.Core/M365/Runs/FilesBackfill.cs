using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Zyggy.Core.M365.Graph;
using Zyggy.Core.M365.Mcp;
using Zyggy.Core.M365.Tools;
using Zyggy.Core.Models;
using Zyggy.Core.Secrets;

namespace Zyggy.Core.M365.Runs;

/// <summary>
/// The files backfill — <c>files-backfill.sh</c> (spec 23 Q8, spec 33 AC-32): every file of the OneDrive and the granted sites' libraries
/// becomes validated fact lines, oldest change first, in resumable, cost-capped batches. This code lists, filters and keeps the cursor
/// (<c>&lt;ISO&gt;|&lt;item-id&gt;</c>, strictly greater next time); the batch's model only downloads each file it is given into the batch's run
/// directory, parses it and writes facts. A batch without a confirming counts line stops that drive with the cursor unmoved.
/// </summary>
internal sealed partial class FilesBackfill(M365Session session, M365ToolPartition partition, IGraphReader reader, IModelRunner model, TimeProvider clock)
{
    private const string Prefix = "files-backfill: ";
    private const string Runbook = "runbook 13 \"Model run failed\"";
    private const string Grant = "runbook 13 \"Grant another site\"";
    private static readonly string[] Types = ["docx", "xlsx", "pptx", "pdf", "txt", "md", "csv", "json", "html", "htm"];
    private static readonly string[] SkipKeys = ["type", "size", "path", "parse_error", "secret_pattern"];

    private readonly List<string> _stdout = [];
    private readonly List<string> _stderr = [];

    private M365Paths Paths => session.Instance.Paths;

    public async Task<RunOutcome> RunAsync(string? driveArgument, bool reset, CancellationToken cancellationToken, int interruptedExit = 130)
    {
        var config = session.Configuration;
        var block = config.Root.GetProperty("files_backfill");
        var batch = (long)block.GetProperty("batch_files").GetDouble();
        if (batch < 1)
        {
            return Die(3, "configuration error: files_backfill.batch_files must be at least 1");
        }

        var budgetTotalText = block.GetProperty("budget_usd_total").GetRawText();
        var budgetTotal = double.Parse(budgetTotalText, CultureInfo.InvariantCulture);
        var maxFacts = (long)block.GetProperty("max_facts").GetDouble();
        var fileMax = (long)block.GetProperty("file_max_bytes").GetDouble();
        if (config.ExcludePaths.Any(p => !ExcludePath().IsMatch(p)))
        {
            return Die(3, "configuration error: drives.exclude_paths holds an entry that is not an absolute path without commas (/…)");
        }

        var skip = config.ExcludePaths.Select(p => p.EndsWith('/') ? p[..^1] : p).ToList();
        var now = Iso(clock.GetUtcNow());
        var state = new M365State(Paths, clock);

        // 3. The identity and the drives.
        var signIn = await reader.SignInAsync(cancellationToken).ConfigureAwait(false);
        if (reader.KeySource != CredentialSource.None)
        {
            _stderr.Add("key: " + reader.KeySource.ToText());
        }

        if (signIn is not null)
        {
            return Die(signIn.ExitCode, signIn.Message);
        }

        var all = await reader.DrivesAsync(cancellationToken).ConfigureAwait(false);
        if (all.Failure is { } failure)
        {
            return Die(failure.ExitCode, failure.Message);
        }

        var drives = all.Value!;
        var excluded = config.ExcludeDrives.Count;
        List<GraphDrive> selected;
        if (driveArgument is not null)
        {
            var lower = Guard.JsonView.AsciiLower(driveArgument);
            var match = drives.FirstOrDefault(d => d.Id == driveArgument || Guard.JsonView.AsciiLower(d.Name ?? string.Empty) == lower);
            if (match is null)
            {
                return config.ExcludeDrives.Any(e => Guard.JsonView.AsciiLower(e) == lower)
                    ? Die(4, $"drive {driveArgument} is excluded (drives.exclude_drives)")
                    : Die(4, $"no drive {driveArgument} among the granted drives");
            }

            selected = [match];
        }
        else
        {
            selected = [.. drives];
        }

        if (drives.Any(d => !M365Grammar.DriveId().IsMatch(d.Id) || !M365Grammar.StateArgument().IsMatch(d.Id)))
        {
            return Die(6, "Graph returned an unexpected drive id");
        }

        // 4. The checkpoint.
        var path = Paths.StateFile("files-backfill.json");
        if (reset)
        {
            var ids = drives.Select(d => d.Id).ToList();
            if (File.Exists(path) && TryItems(path) is { } known)
            {
                ids.AddRange(known);
            }

            foreach (var id in ids.Distinct(StringComparer.Ordinal).Where(i => M365Grammar.StateArgument().IsMatch(i)))
            {
                state.Reset(Watermark(id));
            }

            File.Delete(path);
            Say("checkpoint and files-backfill watermarks reset");
        }

        var (checkpoint, checkpointError) = BackfillCheckpoint.Load(path, Paths.StateDirectory, "drives", "files-backfill", () => new JsonObject
        {
            ["drives"] = new JsonObject(),
            ["total_listed"] = 0,
            ["total_parsed"] = 0,
            ["total_skipped"] = 0,
            ["total_skipped_by"] = SkippedBy(),
            ["total_facts"] = 0,
            ["total_duplicates"] = 0,
            ["total_refused"] = 0,
            ["total_batches"] = 0,
            ["total_turns"] = 0,
            ["total_cost"] = 0,
            ["started"] = now,
            ["updated"] = now,
        });
        if (checkpoint is null)
        {
            return Die(3, checkpointError!);
        }

        // 5. The batches.
        string? stop = null;
        var stuck = new List<string>();
        var forbidden = 0;
        foreach (var drive in selected)
        {
            var name = drive.Name ?? drive.Id;
            if (checkpoint.Items("drives")[drive.Id] is JsonObject entry && entry["done"]?.GetValue<bool>() == true)
            {
                continue;
            }

            stop = CapReason(checkpoint, budgetTotal, budgetTotalText, maxFacts);
            if (stop is not null)
            {
                break;
            }

            // The pre-check: a drive the identity cannot read (no grant) is skipped before any model run.
            var root = await reader.GetAsync($"/drives/{drive.Id}/root", status => status is 403 or 404, cancellationToken).ConfigureAwait(false);
            if (root.Failure is { } rootFailure)
            {
                return Die(rootFailure.ExitCode, rootFailure.Message);
            }

            if (root.Value!.Status is < 200 or > 299)
            {
                SkipForbidden(checkpoint, drive, name, $"{root.Value.Status} ({(root.Value.Status == 403 ? "not granted" : "not found")})", ref forbidden);
                continue;
            }

            var listing = await reader.DriveFilesAsync(drive.Id, cancellationToken).ConfigureAwait(false);
            if (listing.Failure is { ExitCode: 5 } refused)
            {
                SkipForbidden(checkpoint, drive, name, refused.Message.Replace($"drive {drive.Id}: ", string.Empty, StringComparison.Ordinal), ref forbidden);
                continue;
            }

            if (listing.Failure is { } listingFailure)
            {
                return Die(listingFailure.ExitCode, listingFailure.Message);
            }

            var files = listing.Value!;
            var announced = false;
            while (true)
            {
                var wm = (state.Get(Watermark(drive.Id)) ?? string.Empty).TrimEnd('\n');
                if (!announced)
                {
                    var from = wm.Length == 0 ? "the beginning" : wm;
                    Say(BackfillCheckpoint.Long(checkpoint.Items("drives")[drive.Id]?["batches"]) > 0
                        ? $"resuming drive {name} from {from}"
                        : $"starting drive {name} from {from}");
                    announced = true;
                }

                var walk = NextBatch(files, wm, batch, fileMax, skip);
                if (walk.Count == 0)
                {
                    Add(checkpoint, drive, name, string.Empty, finished: true, forbidden: false, Delta.Zero);
                    Say($"{name} done");
                    break;
                }

                // The walk: the skips, the eligible files' prompt lines, the cursor after the last file walked.
                var lines = new StringBuilder();
                int ok = 0, skType = 0, skSize = 0, skPath = 0;
                var cursor = string.Empty;
                foreach (var (cls, file, ext) in walk)
                {
                    if (!M365Grammar.ItemId().IsMatch(file.Id))
                    {
                        return Die(6, "Graph returned an unexpected item id");
                    }

                    if (!M365Grammar.Iso().IsMatch(file.Modified))
                    {
                        return Die(6, "Graph returned an unexpected lastModifiedDateTime");
                    }

                    cursor = file.Modified + "|" + file.Id;
                    switch (cls)
                    {
                        case "ok":
                            ok++;
                            lines.Append(file.Id).Append('\t').Append(ext.Length == 0 ? "-" : ext).Append('\t').Append(file.Modified[..10]).Append('\t').Append(file.Path).Append('\n');
                            break;
                        case "type":
                            skType++;
                            break;
                        case "size":
                            skSize++;
                            break;
                        default:
                            skPath++;
                            break;
                    }
                }

                var walked = walk.Count;
                if (ok == 0)
                {
                    state.Set(Watermark(drive.Id), cursor);
                    Add(checkpoint, drive, name, cursor, false, false, new Delta(0, walked, 0, walked, skType, skSize, skPath, 0, 0, 0, 0, 0, 0, 0));
                    Say($"{name}: {walked} file{(walked == 1 ? string.Empty : "s")} skipped without a model run");
                    continue;
                }

                stop = CapReason(checkpoint, budgetTotal, budgetTotalText, maxFacts);
                if (stop is not null)
                {
                    break;
                }

                if (McpServerLaunch.EnsureDownloadRoot(Paths.DownloadRoot) is { } rootError)
                {
                    return Die(3, rootError);
                }

                var runDirectory = RunDirectory.Create(Paths.DownloadRoot, "zyggy-m365-files");
                var prompt = $"/files-backfill {drive.Id} {runDirectory} {ok}\n<zyggy-m365-data>\n{lines}</zyggy-m365-data>";
                ModelRunResult result;
                try
                {
                    result = await model.RunAsync(
                        M365RunRequest.For(M365RunKind.FilesBackfill, prompt, session.Instance, config, partition, runDirectory), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    RunDirectory.Remove(runDirectory);
                    return new RunOutcome(interruptedExit, _stdout, _stderr);
                }

                RunDirectory.Remove(runDirectory);
                if (BackfillResult.NoResult(result))
                {
                    return Die(6, $"claude returned no JSON result (exit {result.ExitCode ?? 0}) — {Runbook}");
                }

                var turns = result.NumTurns ?? 0;
                var cost = (double)(result.CostUsd ?? 0m);
                if (result.Outcome == ModelRunOutcome.Failed)
                {
                    Add(checkpoint, drive, name, string.Empty, false, false, Delta.Zero with { Turns = turns, Cost = cost });
                    return Die(6, $"claude run failed ({result.FailureDetail}) — {Runbook}");
                }

                // The model's counts line confirms the batch: listed = the files it was given, parsed + skipped = listed.
                var counts = (result.ResultText ?? string.Empty).Split('\n').Select(l => CountsLine().Match(l)).LastOrDefault(m => m.Success);
                long G(int i) => long.Parse(counts!.Groups[i].Value, CultureInfo.InvariantCulture);
                if (counts is null || G(1) != ok || G(2) + G(3) != ok)
                {
                    Add(checkpoint, drive, name, string.Empty, false, false, Delta.Zero with { Turns = turns, Cost = cost });
                    _stderr.Add($"{Prefix}{name}: batch of {ok} files not confirmed by a counts line, stopping the drive");
                    stuck.Add(name);
                    break;
                }

                state.Set(Watermark(drive.Id), cursor);
                var skipped = G(3) + skType + skSize + skPath;
                Add(checkpoint, drive, name, cursor, false, false,
                    new Delta(1, walked, G(2), skipped, G(4) + skType, G(5) + skSize, G(6) + skPath, G(7), G(8), G(9), G(10), G(11), turns, cost));
                var batches = BackfillCheckpoint.Long(checkpoint.Items("drives")[drive.Id]!["batches"]);
                Say($"{name} batch {batches}: listed {walked}, parsed {G(2)}, skipped {skipped}, facts {G(9)}, cost {BackfillCheckpoint.Money(cost)}");
            }

            if (stop is not null)
            {
                break;
            }
        }

        // 6. The counts line.
        var r = checkpoint.Root;
        var by = (JsonObject)r["total_skipped_by"]!;
        var word = stop is null && stuck.Count == 0 ? "done" : "stopped";
        _stdout.Add(
            $"{Prefix}{word} — drives {selected.Count} (excluded {excluded}, forbidden {forbidden}), listed {T(r["total_listed"])}, parsed {T(r["total_parsed"])}, " +
            $"skipped {T(r["total_skipped"])} (type {T(by["type"])}, size {T(by["size"])}, path {T(by["path"])}, parse error {T(by["parse_error"])}, secret pattern {T(by["secret_pattern"])}), " +
            $"facts {T(r["total_facts"])} ({T(r["total_duplicates"])} duplicates dropped, {T(r["total_refused"])} refused), batches {T(r["total_batches"])}, " +
            $"turns {T(r["total_turns"])}, cost {BackfillCheckpoint.Money(BackfillCheckpoint.Number(r["total_cost"]))} (cap {budgetTotalText})");
        if (stop is not null)
        {
            return Die(5, "stopped: " + stop);
        }

        return stuck.Count > 0 ? Die(5, "stopped: batch not confirmed in " + string.Join(", ", stuck)) : new RunOutcome(0, _stdout, _stderr);
    }

    // next_batch: the files after the cursor (key "<modified>|<id>", code-point order), up to and including the n-th eligible one.
    private static List<(string Class, DriveFile File, string Ext)> NextBatch(IReadOnlyList<DriveFile> files, string cursor, long n, long fileMax, List<string> skip)
    {
        var rest = files.Where(f => string.CompareOrdinal(f.Modified + "|" + f.Id, cursor) > 0)
            .Select(f => (Class: Classify(f, fileMax, skip), File: f, Ext: Extension(f.Path)))
            .ToList();
        var ok = 0;
        for (var i = 0; i < rest.Count; i++)
        {
            if (rest[i].Class == "ok" && ++ok == n)
            {
                return rest[..(i + 1)];
            }
        }

        return rest;
    }

    private static string Extension(string path)
    {
        var file = path.Split('/')[^1];
        return file.Contains('.', StringComparison.Ordinal) ? Guard.JsonView.AsciiLower(file.Split('.')[^1]) : string.Empty;
    }

    private static string Classify(DriveFile file, long fileMax, List<string> skip)
    {
        if (!Types.Contains(Extension(file.Path)))
        {
            return "type";
        }

        if (file.Size > fileMax)
        {
            return "size";
        }

        if (Controls().IsMatch(file.Path) || file.Path.Contains("zyggy-m365-data", StringComparison.Ordinal))
        {
            return "path";
        }

        return skip.Any(s => file.Path == s || file.Path.StartsWith(s + "/", StringComparison.Ordinal)) ? "path" : "ok";
    }

    private static string? CapReason(BackfillCheckpoint checkpoint, double budgetTotal, string budgetText, long maxFacts)
    {
        var cost = BackfillCheckpoint.Number(checkpoint.Root["total_cost"]);
        var facts = BackfillCheckpoint.Long(checkpoint.Root["total_facts"]);
        if (cost >= budgetTotal)
        {
            return $"budget {BackfillCheckpoint.Money(cost)} USD over cap {budgetText}";
        }

        return maxFacts > 0 && facts >= maxFacts ? $"facts {facts} at cap {maxFacts}" : null;
    }

    private void SkipForbidden(BackfillCheckpoint checkpoint, GraphDrive drive, string name, string status, ref int forbidden)
    {
        Add(checkpoint, drive, name, string.Empty, finished: false, forbidden: true, Delta.Zero);
        _stderr.Add($"{Prefix}drive {name}: {status}, skipped — {Grant}");
        forbidden++;
    }

    // ckpt_add: one batch added to the drive and to the totals.
    private void Add(BackfillCheckpoint checkpoint, GraphDrive drive, string name, string watermark, bool finished, bool forbidden, Delta delta)
    {
        var drives = checkpoint.Items("drives");
        if (drives[drive.Id] is not JsonObject entry)
        {
            entry = new JsonObject
            {
                ["name"] = name,
                ["site"] = drive.Site,
                ["watermark"] = null,
                ["done"] = false,
                ["forbidden"] = false,
                ["batches"] = 0,
                ["listed"] = 0,
                ["parsed"] = 0,
                ["skipped"] = 0,
                ["skipped_by"] = SkippedBy(),
                ["facts"] = 0,
                ["duplicates"] = 0,
                ["refused"] = 0,
                ["turns"] = 0,
                ["cost"] = 0,
            };
            drives[drive.Id] = entry;
        }

        entry["name"] = name;
        if (watermark.Length > 0)
        {
            entry["watermark"] = watermark;
        }

        entry["done"] = finished;
        entry["forbidden"] = forbidden;
        var root = checkpoint.Root;
        foreach (var (key, value) in new (string, double)[]
                 {
                     ("batches", delta.Batches), ("listed", delta.Listed), ("parsed", delta.Parsed), ("skipped", delta.Skipped), ("facts", delta.Facts),
                     ("duplicates", delta.Duplicates), ("refused", delta.Refused), ("turns", delta.Turns), ("cost", delta.Cost),
                 })
        {
            BackfillCheckpoint.Add(entry, key, value);
            BackfillCheckpoint.Add(root, "total_" + key, value);
        }

        var by = new (string, double)[] { ("type", delta.Type), ("size", delta.Size), ("path", delta.Path), ("parse_error", delta.ParseError), ("secret_pattern", delta.Secret) };
        foreach (var (key, value) in by)
        {
            BackfillCheckpoint.Add((JsonObject)entry["skipped_by"]!, key, value);
            BackfillCheckpoint.Add((JsonObject)root["total_skipped_by"]!, key, value);
        }

        root["updated"] = Iso(clock.GetUtcNow());
        checkpoint.Write();
    }

    private static JsonObject SkippedBy() => new() { ["type"] = 0, ["size"] = 0, ["path"] = 0, ["parse_error"] = 0, ["secret_pattern"] = 0 };

    private static List<string>? TryItems(string path)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) is JsonObject root && root["drives"] is JsonObject items ? [.. items.Select(kv => kv.Key)] : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static StateEntry Watermark(string drive) => new("files-backfill-watermark", $"files-backfill-{drive}.watermark", StateGrammar.Cursor);

    private static string T(JsonNode? node) => BackfillCheckpoint.Text(node);

    private static string Iso(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private void Say(string line) => _stdout.Add(Prefix + line);

    private RunOutcome Die(int exit, string message)
    {
        _stderr.Add(Prefix + message);
        return new RunOutcome(exit, _stdout, _stderr);
    }

    [GeneratedRegex(@"\Afiles-backfill batch: listed ([0-9]+), parsed ([0-9]+), skipped ([0-9]+) \(type ([0-9]+), size ([0-9]+), path ([0-9]+), parse error ([0-9]+), secret pattern ([0-9]+)\), facts ([0-9]+) \(([0-9]+) dup, ([0-9]+) refused\)\z", RegexOptions.CultureInvariant)]
    private static partial Regex CountsLine();

    [GeneratedRegex(@"\A/[^,\u0000-\u001f\u007f]{0,199}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ExcludePath();

    [GeneratedRegex("[\\u0000-\\u001f\\u007f-\\u009f]", RegexOptions.CultureInvariant)]
    private static partial Regex Controls();

    private sealed record Delta(long Batches, long Listed, long Parsed, long Skipped, long Type, long Size, long Path, long ParseError, long Secret, long Facts, long Duplicates, long Refused, long Turns, double Cost)
    {
        public static Delta Zero { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    }
}
