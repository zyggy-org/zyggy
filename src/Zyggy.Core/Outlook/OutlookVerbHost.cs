using System.Globalization;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Configuration;
using Zyggy.Core.Verbs;

namespace Zyggy.Core.Outlook;

/// <summary>
/// <c>zyggy outlook policy | count | list | read</c> (spec 58 slice 2): the read tools of the work mailbox for Claude Code on the work
/// laptop, over the internal Outlook access (COM in production, a fake in tests). Every tool reads <c>node.json</c> and the policy
/// record first (the copied <c>work-boundary.json</c> in the node directory, else the <c>outlook.policyConfirmed</c> mirror) and
/// touches Outlook only once both allow a read. Output is JSON on stdout with invariant ISO 8601 dates; mail is printed as data.
/// </summary>
/// <remarks>
/// Slice 3 adds the work mail brief: <c>brief</c> prints the Inbox mail since the last brief with bodies (records nothing),
/// <c>brief-done --until</c> records what was briefed and keeps the reply drafts read from stdin on this laptop
/// (<c>&lt;nodeDir&gt;/work/&lt;tenant&gt;/outlook/brief.json</c>), <c>drafts [&lt;n&gt;]</c> shows them by number. No verb saves anything
/// in Outlook.
/// Exit codes: 0 · 1 folder, item or draft not found · 2 usage · 3 configuration · 5 refused (<c>policy not confirmed</c>, <c>outlook not
/// enabled</c>, a draft text with a link or an address) · 6 Outlook not available, or Outlook asked for access (its security prompt was
/// not answered within the timeout). Reads are not actions (D-Q6): a bus job or an unattended run may call them.
/// </remarks>
public sealed class OutlookVerbHost
{
    private const string Prefix = "outlook: ";
    private const string Usage = " (usage: zyggy outlook policy | count [--folder <name>]... | list [--folder <name>] [--since <date>] [--max <n>] | read <reference>"
        + " | brief [--since <date>] [--max <n>] | brief-done --until <date> [< drafts.json] | drafts [<n>]; each [--config <node.json>])";
    private const string ReadScope = "read";

    private static readonly TimeSpan DefaultSince = TimeSpan.FromDays(7);

    private static readonly JsonWriterOptions Indented = new() { Indented = true, NewLine = "\n" };

    private readonly IReadOnlyDictionary<string, string?> _environment;
    private readonly TimeProvider _clock;
    private readonly TimeZoneInfo _zone;
    private readonly Func<IOutlookAccess> _access;

    /// <summary>Creates the host over the process environment, the system clock, the machine's time zone and the real Outlook access.</summary>
    /// <param name="environment">The process environment.</param>
    public OutlookVerbHost(IReadOnlyDictionary<string, string?> environment)
        : this(environment, TimeProvider.System, TimeZoneInfo.Local, null)
    {
    }

    internal OutlookVerbHost(IReadOnlyDictionary<string, string?> environment, TimeProvider clock, TimeZoneInfo zone, Func<IOutlookAccess>? access)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(zone);
        _environment = environment;
        _clock = clock;
        _zone = zone;
        _access = access ?? RealAccess;
    }

    /// <summary>
    /// Creates the host from a host's services: the <see cref="TimeProvider"/> when one is registered, and the Outlook access a test
    /// registers at the internal seam; the real COM access otherwise.
    /// </summary>
    /// <param name="services">The services, or <see langword="null"/> for the production defaults.</param>
    /// <param name="environment">The process environment.</param>
    /// <returns>The host.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="environment"/> is null.</exception>
    public static OutlookVerbHost FromServices(IServiceProvider? services, IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        IOutlookAccess? access = services?.GetService<IOutlookAccess>();
        return new OutlookVerbHost(environment, services?.GetService<TimeProvider>() ?? TimeProvider.System, TimeZoneInfo.Local, access is null ? null : () => access);
    }

    /// <summary>Runs <c>outlook &lt;verb&gt; …</c>.</summary>
    /// <param name="args">The arguments after <c>outlook</c>.</param>
    /// <param name="io">The console.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The exit code.</returns>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    public async Task<int> RunAsync(IReadOnlyList<string> args, VerbIo io, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(io);
        Arguments? parsed = Arguments.Parse(args, _zone, out string? usageError);
        if (parsed is null)
        {
            await io.Error.WriteAsync(Prefix + usageError + Usage + "\n").ConfigureAwait(false);
            return 2;
        }

        string path = NodeConfigurationReader.ResolvePath(parsed.Config, _environment);
        NodeConfigurationResult configuration = NodeConfigurationReader.Read(path);
        if (configuration.Configuration is not { } node)
        {
            await io.Error.WriteAsync(Prefix + "configuration error: " + configuration.Detail + "\n").ConfigureAwait(false);
            return 3;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var settings = OutlookSettings.FromNodeJson(node.Outlook);
        string copy = OutlookPolicy.CopyPath(node.EffectiveNodeDir);
        string? workBoundary = File.Exists(copy) ? await File.ReadAllTextAsync(copy, cancellationToken).ConfigureAwait(false) : null;
        OutlookPolicyDecision decision = OutlookPolicy.Evaluate(workBoundary, settings);
        if (parsed.Verb == "policy")
        {
            await io.Out.WriteAsync(PolicyText(decision, copy)).ConfigureAwait(false);
            return 0;
        }

        // The brief verbs read the laptop-local state; it is mail-derived, so the read policy guards it as it guards Outlook.
        string statePath = OutlookDrafts.StatePath(node.EffectiveNodeDir, node.Tenant);
        if (parsed.Verb is "brief-done" or "drafts" && decision.RefusalFor(ReadScope) is { } refusal)
        {
            await io.Error.WriteAsync(Prefix + "refused: " + OutlookReader.Describe(refusal) + "\n").ConfigureAwait(false);
            return 5;
        }

        if (parsed.Verb == "drafts")
        {
            return await DraftsAsync(statePath, parsed.Number, io).ConfigureAwait(false);
        }

        IReadOnlyList<DraftRequest>? requests = null;
        if (parsed.Verb == "brief-done")
        {
            string? stdin = io.InputRedirected ? await io.In.ReadToEndAsync(cancellationToken).ConfigureAwait(false) : null;
            requests = DraftRequest.Parse(stdin, out string? stdinError);
            if (requests is null)
            {
                await io.Error.WriteAsync(Prefix + "drafts on stdin: " + stdinError + " (expected { \"drafts\": [ { \"reference\": \"…\", \"text\": \"…\" } ] })\n").ConfigureAwait(false);
                return 2;
            }
        }

        // The policy is checked before the access exists (AC-5): the COM object is created only inside a read the policy allowed.
        var reader = new OutlookReader(_access(), decision, settings, _zone);
        try
        {
            return parsed.Verb switch
            {
                "count" => await CountAsync(reader, parsed, io).ConfigureAwait(false),
                "list" => await ListAsync(reader, parsed, io).ConfigureAwait(false),
                "brief" => await BriefAsync(reader, parsed, statePath, settings, io).ConfigureAwait(false),
                "brief-done" => await BriefDoneAsync(reader, parsed.Until!.Value, requests!, statePath, io).ConfigureAwait(false),
                _ => await ReadAsync(reader, parsed, io).ConfigureAwait(false),
            };
        }
        catch (OutlookPromptTimeoutException ex)
        {
            await io.Error.WriteAsync(Prefix + "Outlook asked for access: " + ex.Message + "; answer the prompt in Outlook or stop\n").ConfigureAwait(false);
            return 6;
        }
    }

    private static IOutlookAccess RealAccess() =>
        OperatingSystem.IsWindows() ? new OutlookComAccess() : new OutlookAccessUnavailable("Outlook runs on Windows only");

    private static string PolicyText(OutlookPolicyDecision decision, string copy)
    {
        var text = new StringBuilder();
        text.Append("policy: ").Append(decision.Source switch
        {
            OutlookPolicySource.WorkBoundaryFile => OutlookPolicy.WorkBoundaryFileName + " copy at " + copy,
            OutlookPolicySource.NodeJsonMirror => "node.json mirror (outlook.policyConfirmed)",
            _ => "none (no " + OutlookPolicy.WorkBoundaryFileName + " copy at " + copy + ", no outlook.policyConfirmed in node.json)",
        }).Append('\n');
        if (decision.Record is { } record)
        {
            text.Append("date: ").Append(record.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('\n');
            text.Append("scope: ").Append(string.Join(", ", OutlookPolicyRecord.KnownScopes.Where(record.Scope.Contains))).Append('\n');
        }

        text.Append("enabled: ").Append(decision.Enabled ? "true" : "false").Append('\n');
        text.Append("read: ").Append(decision.RefusalFor(ReadScope) is { } refusal ? "refused (" + OutlookReader.Describe(refusal) + ")" : "allowed").Append('\n');
        return text.ToString();
    }

    private static async Task<int> CountAsync(OutlookReader reader, Arguments parsed, VerbIo io)
    {
        OutlookResult<IReadOnlyList<OutlookFolderCount>> result = reader.Count(parsed.Folders);
        if (result.Value is not { } counts)
        {
            return await RefusedAsync(result, io).ConfigureAwait(false);
        }

        await WriteJsonAsync(io, writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartArray("folders");
            foreach (OutlookFolderCount count in counts)
            {
                writer.WriteStartObject();
                writer.WriteString("folder", count.Folder);
                writer.WriteNumber("count", count.Count);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }).ConfigureAwait(false);
        return 0;
    }

    private async Task<int> ListAsync(OutlookReader reader, Arguments parsed, VerbIo io)
    {
        string folder = parsed.Folders.Count == 0 ? "Inbox" : parsed.Folders[0];
        DateTimeOffset since = parsed.Since ?? TimeZoneInfo.ConvertTime(_clock.GetUtcNow() - DefaultSince, _zone);
        OutlookResult<IReadOnlyList<OutlookMailSummary>> result = reader.List(folder, since, parsed.Max);
        if (result.Value is not { } items)
        {
            return await RefusedAsync(result, io).ConfigureAwait(false);
        }

        await WriteJsonAsync(io, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("folder", folder);
            writer.WriteString("since", since.ToString(OutlookDates.Format, CultureInfo.InvariantCulture));
            writer.WriteStartArray("items");
            foreach (OutlookMailSummary item in items)
            {
                writer.WriteStartObject();
                WriteSummary(writer, item);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> ReadAsync(OutlookReader reader, Arguments parsed, VerbIo io)
    {
        OutlookResult<OutlookMail> result = reader.Read(parsed.Reference!);
        if (result.Value is not { } mail)
        {
            return await RefusedAsync(result, io).ConfigureAwait(false);
        }

        await WriteJsonAsync(io, writer => WriteMail(writer, mail)).ConfigureAwait(false);
        return 0;
    }

    // The mail since the last brief (or --since), newest first, each with its body as read: the material of the session's brief.
    // Records nothing; brief-done records what was briefed once the owner has seen it.
    private async Task<int> BriefAsync(OutlookReader reader, Arguments parsed, string statePath, OutlookSettings settings, VerbIo io)
    {
        OutlookBriefState state = OutlookDrafts.Load(statePath);
        DateTimeOffset since = parsed.Since
            ?? (state.BriefedUntil is { } until ? until.AddSeconds(1) : TimeZoneInfo.ConvertTime(_clock.GetUtcNow() - DefaultSince, _zone));
        int cap = Math.Min(parsed.Max ?? settings.ReadMaxItems, settings.ReadMaxItems);
        OutlookResult<IReadOnlyList<OutlookMailSummary>> listed = reader.List("Inbox", since, cap);
        if (listed.Value is not { } summaries)
        {
            return await RefusedAsync(listed, io).ConfigureAwait(false);
        }

        var mails = new List<OutlookMail>();
        foreach (OutlookMailSummary summary in summaries)
        {
            OutlookResult<OutlookMail> read = reader.Read(summary.Reference);
            if (read.Value is not { } mail)
            {
                return await RefusedAsync(read, io).ConfigureAwait(false);
            }

            mails.Add(mail);
        }

        string sinceText = since.ToString(OutlookDates.Format, CultureInfo.InvariantCulture);
        await WriteJsonAsync(io, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("folder", "Inbox");
            writer.WriteString("since", sinceText);
            writer.WriteString("until", mails.Count == 0 ? sinceText : mails[0].Summary.Received);
            writer.WriteBoolean("more", mails.Count >= cap);
            writer.WriteStartArray("items");
            foreach (OutlookMail mail in mails)
            {
                WriteMail(writer, mail);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }).ConfigureAwait(false);
        return 0;
    }

    // Records what was briefed and keeps the brief's reply drafts on this laptop, replacing the last brief's: each answers the sender
    // of its mail, with the quoted history as received. Every written text is checked before Outlook is read; nothing is saved in
    // Outlook (no such call exists).
    private static async Task<int> BriefDoneAsync(OutlookReader reader, DateTimeOffset until, IReadOnlyList<DraftRequest> requests, string statePath, VerbIo io)
    {
        for (var i = 0; i < requests.Count; i++)
        {
            if (OutlookDrafts.WrittenTextProblem(requests[i].Text) is { } problem)
            {
                await io.Error.WriteAsync(Prefix + "refused: draft " + (i + 1).ToString(CultureInfo.InvariantCulture) + " " + problem + "\n").ConfigureAwait(false);
                return 5;
            }
        }

        var drafts = new List<OutlookDraft>();
        for (var i = 0; i < requests.Count; i++)
        {
            OutlookResult<OutlookMail> read = reader.Read(requests[i].Reference);
            if (read.Value is not { } mail)
            {
                if (read.Refusal == OutlookRefusal.ItemNotFound)
                {
                    await io.Error.WriteAsync(Prefix + "draft " + (i + 1).ToString(CultureInfo.InvariantCulture) + ": " + read.Message + "\n").ConfigureAwait(false);
                    return 1;
                }

                return await RefusedAsync(read, io).ConfigureAwait(false);
            }

            OutlookMailSummary summary = mail.Summary;
            drafts.Add(new OutlookDraft(i + 1, summary.Reference, summary.SenderName, summary.SenderAddress, OutlookDrafts.ReplySubject(summary.Subject), requests[i].Text, OutlookDrafts.Quote(mail)));
        }

        OutlookDrafts.Save(statePath, new OutlookBriefState(until, drafts));
        await io.Out.WriteAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"{Prefix}brief recorded until {until.ToString(OutlookDates.Format, CultureInfo.InvariantCulture)}; {drafts.Count} reply drafts kept on this laptop (zyggy outlook drafts)\n")).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> DraftsAsync(string statePath, int? number, VerbIo io)
    {
        OutlookBriefState state = OutlookDrafts.Load(statePath);
        if (number is { } n && state.Drafts.All(d => d.Number != n))
        {
            await io.Error.WriteAsync(Prefix + "draft not found\n").ConfigureAwait(false);
            return 1;
        }

        await io.Out.WriteAsync(OutlookDrafts.Write(state, number)).ConfigureAwait(false);
        return 0;
    }

    private static void WriteMail(Utf8JsonWriter writer, OutlookMail mail)
    {
        writer.WriteStartObject();
        WriteSummary(writer, mail.Summary);
        writer.WriteStartArray("attachments");
        foreach (string name in mail.AttachmentNames)
        {
            writer.WriteStringValue(name);
        }

        writer.WriteEndArray();
        writer.WriteNumber("attachmentCount", mail.AttachmentCount);
        if (mail.Body is null)
        {
            writer.WriteNull("body");
        }
        else
        {
            writer.WriteString("body", mail.Body);
        }

        writer.WriteBoolean("bodyTruncated", mail.BodyTruncated);
        writer.WriteEndObject();
    }

    private static void WriteSummary(Utf8JsonWriter writer, OutlookMailSummary item)
    {
        writer.WriteString("reference", item.Reference);
        writer.WriteString("senderName", item.SenderName);
        writer.WriteString("senderAddress", item.SenderAddress);
        writer.WriteString("subject", item.Subject);
        writer.WriteString("received", item.Received);
        writer.WriteString("importance", item.Importance);
        writer.WriteString("flag", item.Flag);
        writer.WriteBoolean("isRead", item.IsRead);
        writer.WriteBoolean("hasAttachments", item.HasAttachments);
        writer.WriteBoolean("isProtected", item.IsProtected);
    }

    private static async Task WriteJsonAsync(VerbIo io, Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, Indented))
        {
            write(writer);
        }

        await io.Out.WriteAsync(Encoding.UTF8.GetString(buffer.ToArray()) + "\n").ConfigureAwait(false);
    }

    private static async Task<int> RefusedAsync<T>(OutlookResult<T> result, VerbIo io)
        where T : class
    {
        (string line, int exit) = result.Refusal switch
        {
            OutlookRefusal.PolicyNotConfirmed or OutlookRefusal.NotEnabled => ("refused: " + result.Message, 5),
            OutlookRefusal.OutlookNotAvailable => (result.Message + (result.Detail is null ? string.Empty : " (" + result.Detail + ")"), 6),
            _ => (result.Message!, 1),
        };
        await io.Error.WriteAsync(Prefix + line + "\n").ConfigureAwait(false);
        return exit;
    }

    /// <summary>The parsed arguments: the verb, its options and <c>--config</c>.</summary>
    private sealed record Arguments(string Verb, string? Config, IReadOnlyList<string> Folders, DateTimeOffset? Since, int? Max, string? Reference)
    {
        private static readonly HashSet<string> Verbs = new(["policy", "count", "list", "read", "brief", "brief-done", "drafts"], StringComparer.Ordinal);

        /// <summary>Gets <c>--until</c> of <c>brief-done</c>.</summary>
        public DateTimeOffset? Until { get; init; }

        /// <summary>Gets the draft number of <c>drafts &lt;n&gt;</c>.</summary>
        public int? Number { get; init; }

        public static Arguments? Parse(IReadOnlyList<string> args, TimeZoneInfo zone, out string? error)
        {
            error = null;
            if (args.Count == 0)
            {
                error = "no verb given";
                return null;
            }

            string verb = args[0];
            if (!Verbs.Contains(verb))
            {
                error = $"unknown verb '{ShellText.Prefix(verb, 40)}'";
                return null;
            }

            string? config = null;
            var folders = new List<string>();
            DateTimeOffset? since = null;
            int? max = null;
            string? reference = null;
            DateTimeOffset? until = null;
            int? number = null;
            for (var i = 1; i < args.Count; i++)
            {
                string arg = args[i];
                string? value = i + 1 < args.Count ? args[i + 1] : null;
                switch (arg)
                {
                    case "--config":
                    case "--folder":
                    case "--since":
                    case "--max":
                    case "--until":
                        if (string.IsNullOrEmpty(value))
                        {
                            error = arg + " needs a value";
                            return null;
                        }

                        i++;
                        break;
                    default:
                        if (verb == "read" && reference is null && !arg.StartsWith("--", StringComparison.Ordinal))
                        {
                            reference = arg;
                            continue;
                        }

                        if (verb == "drafts" && number is null && !arg.StartsWith("--", StringComparison.Ordinal))
                        {
                            if (!int.TryParse(arg, NumberStyles.None, CultureInfo.InvariantCulture, out int parsedNumber) || parsedNumber < 1)
                            {
                                error = "drafts takes a draft number (1, 2, …)";
                                return null;
                            }

                            number = parsedNumber;
                            continue;
                        }

                        error = $"unexpected argument '{ShellText.Prefix(arg, 40)}'";
                        return null;
                }

                if (arg == "--until")
                {
                    if (verb != "brief-done" || !OutlookDates.TryParseSince(value!, zone, out DateTimeOffset parsedUntil))
                    {
                        error = "--until takes the brief's until (an ISO 8601 date with offset), for brief-done";
                        return null;
                    }

                    until = parsedUntil;
                    continue;
                }

                switch (arg)
                {
                    case "--config":
                        config = value;
                        break;
                    case "--folder":
                        if (verb is not ("count" or "list"))
                        {
                            error = "--folder is for count and list";
                            return null;
                        }

                        folders.Add(value!);
                        break;
                    case "--since":
                        if (verb is not ("list" or "brief") || !OutlookDates.TryParseSince(value!, zone, out DateTimeOffset parsedSince))
                        {
                            error = "--since takes YYYY-MM-DD or an ISO 8601 date with offset, for list and brief";
                            return null;
                        }

                        since = parsedSince;
                        break;
                    default:
                        if (verb is not ("list" or "brief") || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsedMax) || parsedMax < 1)
                        {
                            error = "--max takes a positive integer, for list and brief";
                            return null;
                        }

                        max = parsedMax;
                        break;
                }
            }

            if (verb == "read" && reference is null)
            {
                error = "read needs <reference>";
                return null;
            }

            if (verb == "list" && folders.Count > 1)
            {
                error = "list takes one --folder";
                return null;
            }

            if (verb == "brief-done" && until is null)
            {
                error = "brief-done needs --until <the brief's until>";
                return null;
            }

            return new Arguments(verb, config, folders, since, max, reference) { Until = until, Number = number };
        }
    }

    /// <summary>One reply draft as the session hands it to <c>brief-done</c> on stdin: the mail it answers and the written text.</summary>
    private sealed record DraftRequest(string Reference, string Text)
    {
        // No stdin (not redirected) or blank stdin is a brief without drafts; anything else must be { "drafts": [ { reference, text } ] }.
        public static List<DraftRequest>? Parse(string? stdin, out string? error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(stdin))
            {
                return [];
            }

            try
            {
                using var document = JsonDocument.Parse(stdin);
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty("drafts", out JsonElement drafts)
                    || drafts.ValueKind != JsonValueKind.Array)
                {
                    error = "no drafts array";
                    return null;
                }

                var requests = new List<DraftRequest>();
                foreach (JsonElement draft in drafts.EnumerateArray())
                {
                    if (draft.ValueKind != JsonValueKind.Object
                        || !draft.TryGetProperty("reference", out JsonElement reference) || reference.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(reference.GetString())
                        || !draft.TryGetProperty("text", out JsonElement text) || text.ValueKind != JsonValueKind.String)
                    {
                        error = "draft " + (requests.Count + 1).ToString(CultureInfo.InvariantCulture) + " needs a reference and a text";
                        return null;
                    }

                    requests.Add(new DraftRequest(reference.GetString()!, text.GetString()!));
                }

                return requests;
            }
            catch (JsonException)
            {
                error = "not JSON";
                return null;
            }
        }
    }
}
