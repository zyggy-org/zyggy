using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

using Zyggy.Core.Brief;
using Zyggy.Core.M365.Audit;
using Zyggy.Core.M365.Graph;
using Zyggy.Core.M365.Mcp;
using Zyggy.Core.M365.Tools;
using Zyggy.Core.Memory;
using Zyggy.Core.Models;
using Zyggy.Core.Secrets;

namespace Zyggy.Core.M365.Runs;

/// <summary>
/// The morning brief (spec 35): one unattended mail run that reads the new mail and the changed files through the m365 server and
/// answers in the schema; the binary checks the answer, numbers the Z items, renders one page and writes the brief file and its item list
/// to the brief state directory — no brief Draft. Reply Drafts stay (at most <c>reply_cap</c>). Then the audit, the watermark, one memory
/// line, the <c>brief.jsonl</c> row and the journal line. Order: identity → idempotence → folders and drives → the pre-pass → a run
/// directory with <c>mail.json</c> → the model run → the run directory removed → the answer parsed and validated → the audit and the
/// receipt → the watermark → the memory line → the sidecar, then the brief file → retention → the record.
/// Exit 0 done or already created · 3 configuration · 5 audit flagged · 6 identity, Graph or model-run failure (no files, watermark unchanged).
/// </summary>
internal sealed class BriefRun(M365Session session, M365ToolPartition partition, IGraphReader reader, IModelRunner model, TimeProvider clock)
{
    private const string Prefix = "m365-brief: ";
    private const string Runbook = "runbook 13 \"Model run failed\"";
    private static readonly StateEntry MailWatermark = new("mail-watermark", "mail-watermark", StateGrammar.Iso);

    private static readonly JsonWriterOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly List<string> _stdout = [];
    private readonly List<string> _stderr = [];
    private string _date = string.Empty;

    private M365Paths Paths => session.Instance.Paths;

    public async Task<RunOutcome> RunAsync(CancellationToken cancellationToken, Func<int?>? signalExit = null)
    {
        var now = clock.GetUtcNow();
        var local = TimeZoneInfo.ConvertTime(now, session.TimeZone);
        var today = DateOnly.FromDateTime(local.DateTime);
        _date = BriefPaths.Iso(today);
        var window = Iso(now);
        var config = session.Configuration;
        var settings = BriefSettings.From(config);
        var briefPaths = BriefPaths.Beside(Paths);
        var store = new BriefStore(briefPaths);

        // 1. The identity, fails fast.
        var signIn = await reader.SignInAsync(cancellationToken).ConfigureAwait(false);
        var key = reader.KeySource == CredentialSource.None ? string.Empty : reader.KeySource.ToText();
        if (key.Length > 0)
        {
            _stderr.Add("key: " + key);
        }

        if (signIn is not null)
        {
            return Fail(signIn.ExitCode, signIn.Message);
        }

        // 2. Idempotence (AC-40): the brief file, or the m365 receipt.
        if (File.Exists(briefPaths.Markdown(today)))
        {
            return AlreadyCreated(string.Empty);
        }

        if (File.Exists(Paths.StateFile($"brief-{_date}.json")))
        {
            return AlreadyCreated(" (brief file missing — runbook 13 \"Brief run failed\")");
        }

        // 3. The mode: the weekend run comes with the ideas run (Step 10); until then every day is a weekday run.
        // 4. The folders, the drives, the pre-pass.
        var folders = await reader.MailFoldersAsync(cancellationToken).ConfigureAwait(false);
        if (folders.Failure is { } foldersFailure)
        {
            return Fail(foldersFailure.ExitCode, foldersFailure.Message);
        }

        var inbox = folders.Value!.FirstOrDefault(f => f.WellKnownName == "inbox")?.Id ?? string.Empty;
        if (!M365Grammar.Id().IsMatch(inbox))
        {
            return Fail(6, "no Inbox folder in the mailbox's folder list");
        }

        var draftsFolder = folders.Value!.FirstOrDefault(f => f.WellKnownName == "drafts")?.Id;
        var drives = await reader.DrivesAsync(cancellationToken).ConfigureAwait(false);
        if (drives.Failure is { } drivesFailure)
        {
            return Fail(drivesFailure.ExitCode, drivesFailure.Message);
        }

        if (drives.Value!.Any(d => !M365Grammar.DriveId().IsMatch(d.Id)))
        {
            return Fail(6, "Graph returned an unexpected drive id");
        }

        var (prepass, prepassFailure) = await new MailPrepass(reader, Paths, config, settings, session.TimeZone, clock)
            .RunAsync(inbox, draftsFolder, cancellationToken).ConfigureAwait(false);
        if (prepassFailure is not null)
        {
            return Fail(prepassFailure.ExitCode, prepassFailure.Message);
        }

        // 5. The run directory with mail.json; the model run.
        if (McpServerLaunch.EnsureDownloadRoot(Paths.DownloadRoot) is { } rootError)
        {
            return Die(3, rootError);
        }

        string runDirectory;
        try
        {
            runDirectory = RunDirectory.Create(Paths.DownloadRoot, $"zyggy-m365-brief-{_date}");
            StateFiles.WriteOwnerOnly(Path.Join(runDirectory, "mail.json"), MailInput.Render(prepass!, session.TimeZone));
        }
        catch (IOException)
        {
            return Die(3, $"cannot create a run directory in {Paths.DownloadRoot}");
        }

        var prompt = string.Join(' ', ["/morning-brief", config.Mailbox, inbox, "attachments=" + (config.AttachmentParse ? "on" : "off"), .. drives.Value!.Select(d => d.Id), runDirectory]);
        ModelRunResult result;
        try
        {
            result = await model.RunAsync(
                M365RunRequest.For(M365RunKind.Brief, prompt, session.Instance, config, partition, runDirectory), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            RunDirectory.Remove(runDirectory);
            return new RunOutcome(signalExit?.Invoke() ?? 143, _stdout, _stderr);
        }

        RunDirectory.Remove(runDirectory);

        // 6. The answer: the caps, then the shape.
        if (CheckResult(result) is { } failed)
        {
            return failed;
        }

        var (output, rejection) = MailRunOutput.TryParse(result.StructuredOutput);
        if (output is null)
        {
            return Fail(6, $"claude run returned no valid brief ({rejection}) — {Runbook}");
        }

        // 7. The Z targets' locations, the validation, the audit, the receipt, the watermark, the memory line.
        var locations = new Dictionary<string, MessageLocation>(StringComparer.Ordinal);
        var targets = output.Mail.Where(e => e.Action == "z" && e.Z is not null)
            .Select(e => e.Z!.Kind == "send" ? e.Z.DraftId : e.Z.Kind == "move" ? e.Id : null)
            .OfType<string>()
            .Where(id => id.Length > 0 && M365Grammar.Id().IsMatch(id))
            .Distinct(StringComparer.Ordinal);
        foreach (var target in targets)
        {
            var location = await reader.MessageLocationAsync(target, cancellationToken).ConfigureAwait(false);
            if (location.Failure is not null)
            {
                return Fail(location.Failure.ExitCode, location.Failure.Message);
            }

            locations[target] = location.Value!;
        }

        var context = new ValidationContext(today, now, inbox, draftsFolder, OwnerName(folders.Value!), locations, session.Patterns, config.SuggestionCap, session.TimeZone);
        var (document, validationRejection) = MailRunValidator.Validate(output, prepass!, context, prepass!.Watermark);
        if (document is null)
        {
            return Fail(6, $"claude run returned no valid brief ({validationRejection}) — {Runbook}");
        }

        var answered = prepass.Mail.Where(m => m.Answered is not null).Select(m => m.Message.ConversationId).ToHashSet(StringComparer.Ordinal);
        var violations = document.AuditReasons.Where(MailRunValidator.IsViolation).ToList();
        var audit = await new DraftAudit(reader, new M365State(Paths, clock), Paths, config, session.Patterns)
            .AuditAsync(today, window, AuditMode.Session, answered, violations, cancellationToken).ConfigureAwait(false);
        string verdict;
        int code;
        switch (audit.Exit)
        {
            case 0:
                (verdict, code) = ("ok", 0);
                break;
            case 5:
                (verdict, code) = ("FLAGGED", 5);
                break;
            default:
                return Fail(audit.Exit, $"audit failed ({audit.Error})");
        }

        if (code == 5)
        {
            document = document with { Audit = "flagged", AuditReasons = [.. document.AuditReasons.Where(r => !MailRunValidator.IsViolation(r)), .. audit.Reasons] };
        }

        if (prepass.Mail.Count > 0 && prepass.Mail.Select(m => Parse(m.Message.Received)).Max() is { } newest)
        {
            new M365State(Paths, clock).Set(MailWatermark, Iso(newest));
        }

        var c = document.Counts;
        var summary = $"mail {c.NewMails} ({c.Urgent} urgent, {c.Important} important, {c.Other} other), files {c.Files + c.FilesOther}, replies {output.Replies}, z {document.Items.Count}, you {document.You.Count}, ideas {document.Ideas.Count}, facts {output.Facts}";
        Remember($"Morning brief {_date} written: {summary}, audit {verdict}");

        // 9. The sidecar, then the brief file; retention; the record.
        var (markdown, pageExceeded) = BriefRenderer.RenderMarkdown(document, config.PageMaxLines, config.PageMaxChars);
        document = document with { PageExceeded = pageExceeded };
        try
        {
            store.WriteAtomically(briefPaths.Sidecar(today), BriefSidecar.From(document).ToBytes());
            store.WriteAtomically(briefPaths.Markdown(today), Encoding.UTF8.GetBytes(markdown));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(3, $"cannot write the brief files in {briefPaths.Directory}: {ex.Message}");
        }

        Prune(briefPaths, store, today, settings.KeepDays, now);

        var denials = string.Join(',', result.PermissionDenialTools.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        var turns = result.NumTurns ?? 0;
        var cost = (result.CostUsd ?? 0m).ToString("0.00", CultureInfo.InvariantCulture);
        var zDropped = document.AuditReasons.FirstOrDefault(r => r.EndsWith("left to the owner", StringComparison.Ordinal)) is { } dropped
            ? int.Parse(dropped.Split(' ')[0], CultureInfo.InvariantCulture)
            : 0;
        Append(Row(w =>
        {
            w.WriteString("date", _date);
            w.WriteString("ts", Iso(clock.GetUtcNow()));
            w.WriteNumber("mail", c.NewMails);
            w.WriteNumber("files", c.Files + c.FilesOther);
            w.WriteNumber("replies", output.Replies);
            w.WriteNumber("z", document.Items.Count);
            w.WriteNumber("you", document.You.Count);
            w.WriteNumber("ideas", document.Ideas.Count);
            w.WriteNumber("facts", output.Facts);
            w.WriteNumber("turns", turns);
            w.WritePropertyName("cost");
            w.WriteRawValue(cost);
            w.WriteString("audit", verdict);
            w.WriteStartArray("denials");
            foreach (var tool in denials.Length == 0 ? [] : denials.Split(','))
            {
                w.WriteStringValue(tool);
            }

            w.WriteEndArray();
            w.WriteString("key", key);
            w.WriteNumber("exit", code);
            w.WriteString("mode", "weekday");
            w.WriteNumber("z_dropped", zDropped);
            w.WriteNumber("ideas_dropped", 0);
            w.WriteNumber("ideas_turns", 0);
            w.WritePropertyName("ideas_cost");
            w.WriteRawValue("0");
            w.WriteNull("ideas_exit");
            w.WriteStartObject("counts");
            w.WriteNumber("urgent", c.Urgent);
            w.WriteNumber("important", c.Important);
            w.WriteNumber("other", c.Other);
            w.WriteNumber("files", c.Files);
            w.WriteNumber("filesOther", c.FilesOther);
            w.WriteEndObject();
            w.WriteBoolean("page_exceeded", pageExceeded);
        }));

        var line = $"brief {_date}: {summary}, turns {turns}, cost {cost}, audit {verdict}";
        if (denials.Length > 0)
        {
            line += ", denials " + denials;
        }

        if (pageExceeded)
        {
            line += ", page exceeded";
        }

        _stdout.Add($"{line}, exit {code}");
        return new RunOutcome(code, _stdout, _stderr);
    }

    // The mailbox owner's display name, as the folder listing's owner is not exposed: the configured mailbox's local part when nothing better is known.
    private string OwnerName(IReadOnlyList<MailFolder> folders)
    {
        _ = folders;
        return session.Configuration.Root.TryGetProperty("owner_name", out var name) && name.ValueKind == JsonValueKind.String && name.GetString()!.Length > 0
            ? name.GetString()!
            : session.Configuration.Mailbox;
    }

    private RunOutcome? CheckResult(ModelRunResult result)
    {
        if (result.Outcome == ModelRunOutcome.Failed)
        {
            return result.FailureDetail is ModelFailureDetail.NoResult or ModelFailureDetail.UnparseableResult or ModelFailureDetail.OutputTooLarge or ModelFailureDetail.NotFound or ModelFailureDetail.StartFailed
                ? Fail(6, $"claude returned no JSON result (exit {result.ExitCode ?? 0}) — {Runbook}")
                : Fail(6, $"claude run failed ({result.FailureDetail}) — {Runbook}");
        }

        var block = session.Configuration.Root.GetProperty("brief");
        var maxTurns = (int)block.GetProperty("max_turns").GetDouble();
        var budgetText = block.GetProperty("budget_usd").GetRawText();
        var budget = decimal.Parse(budgetText, NumberStyles.Float, CultureInfo.InvariantCulture);
        var turns = result.NumTurns ?? 0;
        var cost = result.CostUsd ?? 0m;
        var costText = cost.ToString(CultureInfo.InvariantCulture);
        if (cost > budget || turns > maxTurns)
        {
            var costPart = cost > budget ? $"cost {costText} > budget {budgetText}" : $"cost {costText} of budget {budgetText}";
            var turnsPart = turns > maxTurns ? $"turns {turns} > {maxTurns}" : $"turns {turns} of {maxTurns}";
            return Fail(6, $"claude run over the cap ({costPart}, {turnsPart}) — {Runbook}");
        }

        return null;
    }

    // AC-42: brief files and run directories older than brief_keep_days; last-shown is never pruned.
    private static void Prune(BriefPaths paths, BriefStore store, DateOnly today, int keepDays, DateTimeOffset now)
    {
        try
        {
            foreach (var date in store.BriefDates().Where(d => d < today.AddDays(-keepDays)))
            {
                File.Delete(paths.Markdown(date));
                File.Delete(paths.Sidecar(date));
            }

            foreach (var file in Directory.EnumerateFiles(paths.Directory, "brief-*.json"))
            {
                if (BriefPaths.TryParseDate(Path.GetFileName(file), out var date, out _) && date < today.AddDays(-keepDays))
                {
                    File.Delete(file);
                }
            }

            if (Directory.Exists(paths.RunsDirectory))
            {
                foreach (var run in Directory.EnumerateDirectories(paths.RunsDirectory))
                {
                    if (Directory.GetLastWriteTimeUtc(run) < now.UtcDateTime.AddDays(-keepDays))
                    {
                        RunDirectory.Remove(run);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Retention is best effort; the next run tries again.
        }
    }

    // The brief's one memory line, written as remember does even under ZYGGY_HOOKS=off; a refusal is reported, never fatal.
    private void Remember(string fact)
    {
        try
        {
            var outcome = new RememberService(session.Memory, session.TimeZone, clock, session.Patterns)
                .Remember(new RememberRequest("observed", string.Empty, $"m365-brief {_date}", TextCollapse.Line(fact)));
            if (outcome.RefusedBy is { } pattern)
            {
                _stderr.Add($"{Prefix}remember failed (exit 2): refused: matches secret pattern {pattern}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _stderr.Add($"{Prefix}remember failed (exit 1): {ex.Message}");
        }
    }

    private RunOutcome AlreadyCreated(string note)
    {
        _stdout.Add($"brief {_date}: already created{note}");
        return new RunOutcome(0, _stdout, _stderr);
    }

    // A failure after the pre-flight started: recorded in brief.jsonl, then one stderr line.
    private RunOutcome Fail(int exit, string message)
    {
        Append(Row(w =>
        {
            w.WriteString("date", _date);
            w.WriteString("ts", Iso(clock.GetUtcNow()));
            w.WriteNumber("exit", exit);
            w.WriteString("error", message);
        }));
        return Die(exit, message);
    }

    private RunOutcome Die(int exit, string message)
    {
        _stderr.Add(Prefix + message);
        return new RunOutcome(exit, _stdout, _stderr);
    }

    private void Append(string row)
    {
        StateFiles.EnsureDirectory(Paths.StateDirectory);
        var path = Paths.StateFile("brief.jsonl");
        var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(path, options))
        {
            stream.Write(Encoding.UTF8.GetBytes(row + "\n"));
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static string Row(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, Compact))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static DateTimeOffset? Parse(string iso) =>
        DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at) ? at : null;

    private static string Iso(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}

/// <summary><c>zy_m365_run_dir</c>: a fresh 0700 <c>&lt;name&gt;.XXXXXX</c> under the download root, and its removal.</summary>
internal static class RunDirectory
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    /// <exception cref="IOException">Thrown when no directory could be created.</exception>
    public static string Create(string root, string name)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var path = Path.Join(root, name + "." + RandomNumberGenerator.GetString(Alphabet, 6));
            if (Directory.Exists(path) || File.Exists(path))
            {
                continue;
            }

            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(path);
            }
            else
            {
                Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            return path;
        }

        throw new IOException($"cannot create a run directory in {root}");
    }

    public static void Remove(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // rm -rf: what cannot be removed stays.
        }
    }
}
