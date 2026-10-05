using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Zyggy.Core.M365.Graph;
using Zyggy.Core.M365.Tools;
using Zyggy.Core.Models;
using Zyggy.Core.Secrets;

namespace Zyggy.Core.M365.Runs;

/// <summary>
/// The whole-mailbox backfill — <c>mail-backfill.sh</c> (spec 23 Q7, spec 33 AC-31): every mail of the mailbox becomes validated fact
/// lines in memory <c>inbox/</c>, newest first, in resumable, cost-capped batches. Per folder, until a batch lists 0 messages: one model
/// run (<c>/mail-backfill &lt;mailbox&gt; &lt;folder-id&gt; &lt;watermark&gt; &lt;batch&gt;</c>), the new watermark read back from the state,
/// the checkpoint rewritten. The totals are checked before every batch; a watermark that did not move stops that folder; a stop writes
/// nothing and the checkpoint of the last completed batch stands.
/// </summary>
internal sealed partial class MailBackfill(M365Session session, M365ToolPartition partition, IGraphReader reader, IModelRunner model, TimeProvider clock)
{
    private const string Prefix = "mail-backfill: ";
    private const string Runbook = "runbook 13 \"Model run failed\"";

    private readonly List<string> _stdout = [];
    private readonly List<string> _stderr = [];

    private M365Paths Paths => session.Instance.Paths;

    public async Task<RunOutcome> RunAsync(string? folderArgument, bool reset, CancellationToken cancellationToken, Func<int?>? signalExit = null)
    {
        var block = session.Configuration.Root.GetProperty("mail_backfill");
        var batch = (long)block.GetProperty("batch_messages").GetDouble();
        if (batch < 1)
        {
            return Die(3, "configuration error: mail_backfill.batch_messages must be at least 1");
        }

        var budgetTotalText = block.GetProperty("budget_usd_total").GetRawText();
        var budgetTotal = double.Parse(budgetTotalText, CultureInfo.InvariantCulture);
        var maxFacts = (long)block.GetProperty("max_facts").GetDouble();
        var maxMessages = (long)block.GetProperty("max_messages").GetDouble();
        var now = Iso(clock.GetUtcNow());
        var state = new M365State(Paths, clock);

        // 3. The identity and the folders.
        var signIn = await reader.SignInAsync(cancellationToken).ConfigureAwait(false);
        if (reader.KeySource != CredentialSource.None)
        {
            _stderr.Add("key: " + reader.KeySource.ToText());
        }

        if (signIn is not null)
        {
            return Die(signIn.ExitCode, signIn.Message);
        }

        var all = await reader.MailFoldersAsync(cancellationToken).ConfigureAwait(false);
        if (all.Failure is { } failure)
        {
            return Die(failure.ExitCode, failure.Message);
        }

        var folders = all.Value!;
        var excluded = folders.Count(f => f.Excluded);
        List<MailFolder> selected;
        if (folderArgument is not null)
        {
            var lower = Guard.JsonView.AsciiLower(folderArgument);
            var match = folders.FirstOrDefault(f => f.Id == folderArgument || Guard.JsonView.AsciiLower(f.DisplayName ?? string.Empty) == lower
                || Guard.JsonView.AsciiLower(f.WellKnownName ?? string.Empty) == lower);
            if (match is null)
            {
                return Die(4, $"no folder {folderArgument} in the mailbox");
            }

            if (match.Excluded)
            {
                return Die(4, $"folder {folderArgument} is excluded (mail_backfill.exclude_folders)");
            }

            selected = [match];
        }
        else
        {
            selected = [.. folders.Where(f => !f.Excluded)];
        }

        if (folders.Any(f => !M365Grammar.Id().IsMatch(f.Id) || !M365Grammar.StateArgument().IsMatch(f.Id)))
        {
            return Die(6, "Graph returned an unexpected folder id");
        }

        // 4. The checkpoint.
        var path = Paths.StateFile("mail-backfill.json");
        if (reset)
        {
            var ids = folders.Select(f => f.Id).ToList();
            if (File.Exists(path) && TryItems(path) is { } known)
            {
                ids.AddRange(known);
            }

            foreach (var id in ids.Distinct(StringComparer.Ordinal).Where(i => M365Grammar.StateArgument().IsMatch(i)))
            {
                state.Reset(Watermark(id));
            }

            File.Delete(path);
            Say("checkpoint and backfill watermarks reset");
        }

        var (checkpoint, checkpointError) = BackfillCheckpoint.Load(path, Paths.StateDirectory, "folders", "mail-backfill", () => new JsonObject
        {
            ["folders"] = new JsonObject(),
            ["total_messages"] = 0,
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
        foreach (var folder in selected)
        {
            var name = folder.DisplayName ?? folder.Id;
            if (checkpoint.Items("folders")[folder.Id] is JsonObject entry && entry["done"]?.GetValue<bool>() == true)
            {
                continue;
            }

            var announced = false;
            while (true)
            {
                stop = CapReason(checkpoint, budgetTotal, budgetTotalText, maxFacts, maxMessages);
                if (stop is not null)
                {
                    break;
                }

                var wm = (state.Get(Watermark(folder.Id)) ?? string.Empty).TrimEnd('\n');
                if (wm.Length == 0)
                {
                    wm = now;
                }

                if (!announced)
                {
                    Say(BackfillCheckpoint.Long(checkpoint.Items("folders")[folder.Id]?["batches"]) > 0
                        ? $"resuming folder {name} from {wm}"
                        : $"starting folder {name} from {wm}");
                    announced = true;
                }

                var prompt = $"/mail-backfill {session.Configuration.Mailbox} {folder.Id} {wm} {batch}";
                ModelRunResult result;
                try
                {
                    result = await model.RunAsync(
                        M365RunRequest.For(M365RunKind.MailBackfill, prompt, session.Instance, session.Configuration, partition, null), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return new RunOutcome(signalExit?.Invoke() ?? 130, _stdout, _stderr);
                }

                if (BackfillResult.NoResult(result))
                {
                    return Die(6, $"claude returned no JSON result (exit {result.ExitCode ?? 0}) — {Runbook}");
                }

                var turns = result.NumTurns ?? 0;
                var cost = (double)(result.CostUsd ?? 0m);
                if (result.Outcome == ModelRunOutcome.Failed)
                {
                    Add(checkpoint, folder.Id, name, string.Empty, false, 0, 0, 0, 0, 0, turns, cost);
                    return Die(6, $"claude run failed ({result.FailureDetail}) — {Runbook}");
                }

                var counts = (result.ResultText ?? string.Empty).Split('\n').Select(l => CountsLine().Match(l)).LastOrDefault(m => m.Success);
                var messages = counts?.Groups[1].Value ?? "-";
                var facts = counts?.Groups[2].Value ?? "-";
                var duplicates = counts is null ? 0 : long.Parse(counts.Groups[3].Value, CultureInfo.InvariantCulture);
                var refused = counts is null ? 0 : long.Parse(counts.Groups[4].Value, CultureInfo.InvariantCulture);
                var newWm = (state.Get(Watermark(folder.Id)) ?? string.Empty).TrimEnd('\n');
                var finished = messages == "0";
                Add(checkpoint, folder.Id, name, newWm, finished, 1, Number(messages), Number(facts), duplicates, refused, turns, cost);
                var batches = BackfillCheckpoint.Long(checkpoint.Items("folders")[folder.Id]!["batches"]);
                Say($"{name} batch {batches}: messages {messages}, facts {facts}, cost {BackfillCheckpoint.Money(cost)}");
                if (finished)
                {
                    Say($"{name} done");
                    break;
                }

                if (newWm.Length == 0 || string.CompareOrdinal(newWm, wm) >= 0)
                {
                    _stderr.Add($"{Prefix}{name}: watermark not advanced, stopping the folder");
                    stuck.Add(name);
                    break;
                }
            }

            if (stop is not null)
            {
                break;
            }
        }

        // 6. The counts line.
        var root = checkpoint.Root;
        var word = stop is null && stuck.Count == 0 ? "done" : "stopped";
        _stdout.Add(
            $"{Prefix}{word} — folders {selected.Count} (excluded {excluded}), messages {T(root["total_messages"])}, batches {T(root["total_batches"])}, " +
            $"facts {T(root["total_facts"])} ({T(root["total_duplicates"])} duplicates dropped, {T(root["total_refused"])} refused), turns {T(root["total_turns"])}, " +
            $"cost {BackfillCheckpoint.Money(BackfillCheckpoint.Number(root["total_cost"]))} (cap {budgetTotalText})");
        if (stop is not null)
        {
            return Die(5, "stopped: " + stop);
        }

        return stuck.Count > 0 ? Die(5, "stopped: watermark not advanced in " + string.Join(", ", stuck)) : new RunOutcome(0, _stdout, _stderr);
    }

    // The reason the totals forbid another batch, or nothing.
    private static string? CapReason(BackfillCheckpoint checkpoint, double budgetTotal, string budgetText, long maxFacts, long maxMessages)
    {
        var root = checkpoint.Root;
        var cost = BackfillCheckpoint.Number(root["total_cost"]);
        var facts = BackfillCheckpoint.Long(root["total_facts"]);
        var messages = BackfillCheckpoint.Long(root["total_messages"]);
        if (cost >= budgetTotal)
        {
            return $"budget {BackfillCheckpoint.Money(cost)} USD over cap {budgetText}";
        }

        if (maxFacts > 0 && facts >= maxFacts)
        {
            return $"facts {facts} at cap {maxFacts}";
        }

        return maxMessages > 0 && messages >= maxMessages ? $"messages {messages} at cap {maxMessages}" : null;
    }

    // ckpt_add: one batch added to the folder and to the totals (a failed batch adds 0 batches, only its turns and cost).
    private void Add(BackfillCheckpoint checkpoint, string id, string name, string watermark, bool finished, long batches, long messages, long facts, long duplicates, long refused, long turns, double cost)
    {
        var folders = checkpoint.Items("folders");
        if (folders[id] is not JsonObject entry)
        {
            entry = new JsonObject
            {
                ["name"] = name,
                ["watermark"] = null,
                ["done"] = false,
                ["batches"] = 0,
                ["messages"] = 0,
                ["facts"] = 0,
                ["duplicates"] = 0,
                ["refused"] = 0,
                ["turns"] = 0,
                ["cost"] = 0,
            };
            folders[id] = entry;
        }

        entry["name"] = name;
        if (watermark.Length > 0)
        {
            entry["watermark"] = watermark;
        }

        entry["done"] = finished;
        foreach (var (key, value) in new (string, double)[] { ("batches", batches), ("messages", messages), ("facts", facts), ("duplicates", duplicates), ("refused", refused), ("turns", turns), ("cost", cost) })
        {
            BackfillCheckpoint.Add(entry, key, value);
            BackfillCheckpoint.Add(checkpoint.Root, "total_" + key, value);
        }

        checkpoint.Root["updated"] = Iso(clock.GetUtcNow());
        checkpoint.Write();
    }

    private static List<string>? TryItems(string path)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) is JsonObject root && root["folders"] is JsonObject items ? [.. items.Select(kv => kv.Key)] : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static StateEntry Watermark(string folder) => new("backfill-watermark", $"backfill-{folder}.watermark", StateGrammar.Iso);

    private static long Number(string value) => value == "-" ? 0 : long.Parse(value, CultureInfo.InvariantCulture);

    private static string T(JsonNode? node) => BackfillCheckpoint.Text(node);

    private static string Iso(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private void Say(string line) => _stdout.Add(Prefix + line);

    private RunOutcome Die(int exit, string message)
    {
        _stderr.Add(Prefix + message);
        return new RunOutcome(exit, _stdout, _stderr);
    }

    [GeneratedRegex(@"\Amail-backfill batch: messages ([0-9]+), facts ([0-9]+) \(([0-9]+) dup, ([0-9]+) refused\)\z", RegexOptions.CultureInvariant)]
    private static partial Regex CountsLine();
}

/// <summary>The result checks the backfills share.</summary>
internal static class BackfillResult
{
    /// <summary>No JSON result from <c>claude</c> at all (the scripts' "returned no JSON result").</summary>
    public static bool NoResult(ModelRunResult result) =>
        result.Outcome == ModelRunOutcome.Failed
        && result.FailureDetail is ModelFailureDetail.NoResult or ModelFailureDetail.UnparseableResult or ModelFailureDetail.OutputTooLarge
            or ModelFailureDetail.NotFound or ModelFailureDetail.StartFailed;
}
