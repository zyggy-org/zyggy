using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

using Zyggy.Core.M365.Audit;
using Zyggy.Core.M365.Graph;
using Zyggy.Core.M365.Mcp;
using Zyggy.Core.M365.Tools;
using Zyggy.Core.Memory;
using Zyggy.Core.Models;
using Zyggy.Core.Secrets;

namespace Zyggy.Core.M365.Runs;

/// <summary>
/// The morning brief — <c>brief.sh</c> (spec 23, spec 33 AC-30): one unattended model run that reads the new mail and the changed files
/// through the m365 server and leaves one brief Draft to the owner, with numbered suggestions, and at most <c>reply_cap</c> reply Drafts;
/// then the audit, one memory line, the <c>brief.jsonl</c> row and the journal line. The run suggests but never acts (D7): its deny list
/// holds the action tools. Order: the identity (fails fast) → idempotence → the Inbox and the drives → a run directory → the model run → the
/// run directory removed → the result checked against the caps → the audit → the memory line → the record.
/// Exit 0 done or already created · 3 configuration · 5 audit flagged · 6 identity, Graph or model-run failure (no receipt).
/// </summary>
internal sealed partial class BriefRun(M365Session session, M365ToolPartition partition, IGraphReader reader, IModelRunner model, TimeProvider clock)
{
    private const string Prefix = "m365-brief: ";
    private const string Subject = "Zyggy — morning brief";
    private const string Runbook = "runbook 13 \"Model run failed\"";

    private static readonly JsonWriterOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly List<string> _stdout = [];
    private readonly List<string> _stderr = [];
    private string _date = string.Empty;

    private M365Paths Paths => session.Instance.Paths;

    public async Task<RunOutcome> RunAsync(CancellationToken cancellationToken, Func<int?>? signalExit = null)
    {
        var now = clock.GetUtcNow();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, session.TimeZone).DateTime);
        _date = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var window = Iso(now);
        var config = session.Configuration;

        // 2. The pre-flight: the identity, idempotence, the Inbox and the drives.
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

        if (File.Exists(Paths.StateFile($"brief-{_date}.json")))
        {
            return AlreadyCreated();
        }

        var midnight = TimeZoneInfo.ConvertTimeToUtc(today.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), session.TimeZone);
        var drafts = await reader.DraftsSinceAsync(Iso(midnight), cancellationToken).ConfigureAwait(false);
        if (drafts.Failure is { } draftsFailure)
        {
            return Fail(draftsFailure.ExitCode, draftsFailure.Message);
        }

        if (drafts.Value!.Any(d => d.TryGetProperty("subject", out var s) && s.ValueKind == JsonValueKind.String && s.GetString() == $"{Subject} {_date}"))
        {
            return AlreadyCreated();
        }

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

        var drives = await reader.DrivesAsync(cancellationToken).ConfigureAwait(false);
        if (drives.Failure is { } drivesFailure)
        {
            return Fail(drivesFailure.ExitCode, drivesFailure.Message);
        }

        if (drives.Value!.Any(d => !M365Grammar.DriveId().IsMatch(d.Id)))
        {
            return Fail(6, "Graph returned an unexpected drive id");
        }

        // 3. The model run, in a fresh run directory removed afterwards (and on a stop).
        if (McpServerLaunch.EnsureDownloadRoot(Paths.DownloadRoot) is { } rootError)
        {
            return Die(3, rootError);
        }

        string runDirectory;
        try
        {
            runDirectory = RunDirectory.Create(Paths.DownloadRoot, $"zyggy-m365-brief-{_date}");
        }
        catch (IOException)
        {
            return Die(3, $"cannot create a run directory in {Paths.DownloadRoot}");
        }

        var prompt = string.Join(' ', ["/morning-brief", config.Mailbox, inbox, .. drives.Value!.Select(d => d.Id), runDirectory]);
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
        if (CheckResult(result) is { } failed)
        {
            return failed;
        }

        var (mail, files, replies, suggestions, facts) = Counts(result.ResultText ?? string.Empty);
        var denials = string.Join(',', result.PermissionDenialTools.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        var turns = result.NumTurns ?? 0;
        var cost = (result.CostUsd ?? 0m).ToString("0.00", CultureInfo.InvariantCulture);

        // 4. The audit, the memory line, the record.
        var paths = Paths;
        var audit = await new DraftAudit(reader, new M365State(paths, clock), paths, config, session.Patterns)
            .AuditAsync(today, window, cancellationToken).ConfigureAwait(false);
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

        var summary = $"mail {mail}, files {files}, replies {replies}, suggestions {suggestions}, facts {facts}";
        Remember($"Morning brief {_date} left as a Draft: {summary}, audit {verdict}");

        Append(Row(w =>
        {
            w.WriteString("date", _date);
            w.WriteString("ts", Iso(clock.GetUtcNow()));
            Count(w, "mail", mail);
            Count(w, "files", files);
            Count(w, "replies", replies);
            Count(w, "suggestions", suggestions);
            Count(w, "facts", facts);
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
        }));

        var line = $"brief {_date}: {summary}, turns {turns}, cost {cost}, audit {verdict}";
        if (denials.Length > 0)
        {
            line += ", denials " + denials;
        }

        _stdout.Add($"{line}, exit {code}");
        return new RunOutcome(code, _stdout, _stderr);
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

    // The model's counts line (its last line of that shape); "-" where it gave none.
    private static (string Mail, string Files, string Replies, string Suggestions, string Facts) Counts(string text)
    {
        var match = text.Split('\n').Select(l => CountsLine().Match(l)).LastOrDefault(m => m.Success);
        return match is null
            ? ("-", "-", "-", "-", "-")
            : (match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value, match.Groups[4].Value, match.Groups[5].Value);
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

    private RunOutcome AlreadyCreated()
    {
        _stdout.Add($"brief {_date}: already created");
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

    // def n: if . == "-" then null else tonumber end
    private static void Count(Utf8JsonWriter writer, string name, string value)
    {
        if (value == "-")
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteNumber(name, long.Parse(value, CultureInfo.InvariantCulture));
        }
    }

    private static string Iso(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string Iso(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"\Abrief [0-9]{4}-[0-9]{2}-[0-9]{2}: mail ([0-9]+), files ([0-9]+), replies ([0-9]+), suggestions ([0-9]+), facts ([0-9]+)\z", RegexOptions.CultureInvariant)]
    private static partial Regex CountsLine();
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
