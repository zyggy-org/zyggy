using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

using Zyggy.Core.M365.Graph;
using Zyggy.Core.Memory;

namespace Zyggy.Core.M365.Audit;

/// <summary>The verdict line (<c>audit ok</c> or <c>audit FLAGGED: …</c>) and exit code, or the Graph/identity failure (no receipt).</summary>
internal sealed record AuditOutcome(int Exit, string? StdoutLine, string? Error)
{
    /// <summary>Gets the reasons of a flagged verdict, in order (empty when ok or failed).</summary>
    public IReadOnlyList<string> Reasons =>
        StdoutLine is { } line && line.StartsWith("audit FLAGGED: ", StringComparison.Ordinal) ? line["audit FLAGGED: ".Length..].Split("; ") : [];
}

/// <summary>33's rule (one brief Draft expected) or 35's (none; the brief is shown in the session).</summary>
internal enum AuditMode
{
    Draft33,
    Session,
}

/// <summary>
/// The post-run audit of the brief's Drafts — <c>verify.sh</c> (spec 23 AC-39, spec 33 AC-23): every Draft created in the window,
/// checked through Graph reads only. Exactly one brief Draft to the owner only; reply Drafts only to the sender or reply-to of a message
/// recorded as replied for the date, in the same conversation; any other Draft to the owner only; no URL and no secret pattern in a
/// Draft's generated text, and no e-mail address outside the brief; at most one brief plus <c>reply_cap</c> Drafts. Writes the receipt
/// <c>brief-&lt;date&gt;.json</c> (0600, no body text).
/// </summary>
internal sealed partial class DraftAudit(IGraphReader reader, M365State state, M365Paths paths, M365Configuration configuration, SecretPatterns patterns)
{
    private const string BriefPrefix = "Zyggy — morning brief";
    private const int SubjectCharacters = 80;

    private static readonly JsonWriterOptions Pretty = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Indented = true, NewLine = "\n" };

    public Task<AuditOutcome> AuditAsync(DateOnly date, string windowIso, CancellationToken cancellationToken) =>
        AuditAsync(date, windowIso, AuditMode.Draft33, new HashSet<string>(StringComparer.Ordinal), [], cancellationToken);

    /// <summary>
    /// Spec 35 AC-20: the session mode — no brief Draft is expected (one is a violation), the cap is <c>reply_cap</c>, a reply Draft to a
    /// mail the owner already answered is a violation, and the validator's violations join the verdict.
    /// </summary>
    public async Task<AuditOutcome> AuditAsync(DateOnly date, string windowIso, AuditMode mode, IReadOnlySet<string> answeredConversations, IReadOnlyList<string> violations, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(answeredConversations);
        ArgumentNullException.ThrowIfNull(violations);
        var dateText = date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        // 3. What Graph holds: the Drafts of the window, the replied-to messages.
        var drafts = await reader.DraftsSinceAsync(windowIso, cancellationToken).ConfigureAwait(false);
        if (drafts.Failure is { } draftsFailure)
        {
            return new AuditOutcome(draftsFailure.ExitCode, null, draftsFailure.Message);
        }

        var repliedIds = Lines(state.Get(new StateEntry("replied", $"replied-{dateText}.ids", StateGrammar.Id)) ?? string.Empty);
        var senders = new List<MessageSender>();
        foreach (var id in repliedIds.Where(i => M365Grammar.Id().IsMatch(i)))
        {
            var sender = await reader.MessageSenderAsync(id, cancellationToken).ConfigureAwait(false);
            if (sender.Value is { } found)
            {
                senders.Add(found);
            }
            else if (sender.Failure is not { ExitCode: 6 } failure || failure.Message != $"not found ({id})")
            {
                return new AuditOutcome(sender.Failure!.ExitCode, null, sender.Failure.Message);
            }
        }

        // 4. The Drafts, in Graph's order.
        var mailbox = JsonLower(configuration.Mailbox);
        var records = drafts.Value!.Select(m => Record(m, mailbox, senders)).ToList();
        var reasons = new List<string>();
        var briefs = 0;
        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index];
            if (record.Reason.Length > 0)
            {
                reasons.Add(record.Reason);
            }

            briefs += record.Kind == "brief" ? 1 : 0;
            var text = GeneratedText(drafts.Value![index], record.Kind);
            if (Url().IsMatch(text))
            {
                reasons.Add($"draft \"{record.Subject}\" contains a URL");
            }

            if (record.Kind != "brief" && Email().IsMatch(text))
            {
                reasons.Add($"draft \"{record.Subject}\" contains an e-mail address");
            }

            if (patterns.TryMatchAnyLine(text, out var secret))
            {
                reasons.Add($"draft \"{record.Subject}\" matches secret pattern {secret}");
            }
        }

        if (mode == AuditMode.Session)
        {
            if (briefs > 0)
            {
                reasons.Add($"{briefs} brief draft{(briefs == 1 ? string.Empty : "s")} (the brief is shown in the session)");
            }

            foreach (var record in records.Where(r => r.Kind == "reply" && r.Conversation.Length > 0 && answeredConversations.Contains(r.Conversation)))
            {
                reasons.Add($"reply draft \"{record.Subject}\" answers a mail the owner already answered");
            }

            var replyCap = ReplyCap();
            if (records.Count > replyCap)
            {
                reasons.Add($"{records.Count} drafts > reply_cap {replyCap}");
            }

            reasons.AddRange(violations);
        }
        else
        {
            if (briefs == 0)
            {
                reasons.Add("no brief draft");
            }
            else if (briefs > 1)
            {
                reasons.Add($"{briefs} brief drafts");
            }

            var cap = ReplyCap() + 1;
            if (records.Count > cap)
            {
                reasons.Add($"{records.Count} drafts > cap {cap} (one brief + reply_cap {cap - 1})");
            }
        }

        // 5. The receipt and the verdict.
        var audit = reasons.Count == 0 ? "ok" : "flagged";
        WriteReceipt(dateText, windowIso, records, repliedIds, audit, reasons);
        return reasons.Count == 0
            ? new AuditOutcome(0, "audit ok", null)
            : new AuditOutcome(5, "audit FLAGGED: " + string.Join("; ", reasons), null);
    }

    private static DraftRecord Record(JsonElement message, string mailbox, List<MessageSender> senders)
    {
        var subject = string.Concat(Controls().Replace(Text(message, "subject"), " ").EnumerateRunes().Take(SubjectCharacters).Select(r => r.ToString()));
        var recipients = Addresses(message, "toRecipients").Concat(Addresses(message, "ccRecipients")).Concat(Addresses(message, "bccRecipients"))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var kind = subject.StartsWith(BriefPrefix, StringComparison.Ordinal) ? "brief" : ReplySubject().IsMatch(subject) ? "reply" : "other";
        var conversation = Text(message, "conversationId");
        var replied = senders.Where(s => s.ConversationId.Length > 0 && s.ConversationId == conversation).ToList();
        var allowed = kind == "reply" ? [mailbox, .. replied.SelectMany(s => new[] { s.From }.Concat(s.ReplyTo))] : new List<string> { mailbox };
        var outside = string.Join(", ", recipients.Where(r => !allowed.Contains(r)));
        var reason = kind == "reply" && replied.Count == 0 ? $"reply draft \"{subject}\" has no recorded replied message"
            : outside.Length == 0 ? string.Empty
            : kind == "brief" ? $"brief draft has recipients other than the owner ({outside})"
            : $"draft \"{subject}\" to {outside} not allowed";
        return new DraftRecord(message.TryGetProperty("id", out var id) ? id.Clone() : default, kind, subject, recipients, reason, conversation);
    }

    // .body.content // .bodyPreview // "", above Outlook's quote separator for a reply (CR removed).
    private static string GeneratedText(JsonElement message, string kind)
    {
        var text = message.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.Object && body.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.String
                ? content.GetString()!
                : Text(message, "bodyPreview");
        if (kind != "reply")
        {
            return text;
        }

        var above = new List<string>();
        foreach (var line in Lines(text))
        {
            var trimmed = line.EndsWith('\r') ? line[..^1] : line;
            if (QuoteSeparator().IsMatch(trimmed))
            {
                break;
            }

            above.Add(trimmed);
        }

        return string.Join('\n', above);
    }

    private void WriteReceipt(string date, string window, List<DraftRecord> records, List<string> repliedIds, string audit, List<string> reasons)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, Pretty))
        {
            writer.WriteStartObject();
            writer.WriteString("date", date);
            writer.WriteString("window_start", window);
            writer.WriteStartArray("drafts");
            foreach (var record in records)
            {
                writer.WriteStartObject();
                writer.WritePropertyName("id");
                if (record.Id.ValueKind == JsonValueKind.Undefined)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    record.Id.WriteTo(writer);
                }

                writer.WriteString("kind", record.Kind);
                writer.WriteString("subject", record.Subject);
                Strings(writer, "recipients", record.Recipients);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            Strings(writer, "replied_ids", repliedIds);
            writer.WriteString("audit", audit);
            Strings(writer, "reasons", reasons);
            writer.WriteEndObject();
        }

        byte[] bytes = [.. buffer.ToArray(), (byte)'\n'];
        StateFiles.WriteAtomically(paths.StateDirectory, paths.StateFile($"brief-{date}.json"), bytes);
    }

    private int ReplyCap() => configuration.Root.TryGetProperty("brief", out var brief) ? (int)brief.GetProperty("reply_cap").GetDouble() : 0;

    private static void Strings(Utf8JsonWriter writer, string name, IEnumerable<string> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    // [.[]?.emailAddress.address // empty | ascii_downcase]
    private static IEnumerable<string> Addresses(JsonElement message, string field) =>
        message.TryGetProperty(field, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray()
                .Select(r => r.ValueKind == JsonValueKind.Object && r.TryGetProperty("emailAddress", out var e) && e.ValueKind == JsonValueKind.Object
                    && e.TryGetProperty("address", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null)
                .OfType<string>()
                .Select(JsonLower)
            : [];

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : string.Empty;

    private static List<string> Lines(string text)
    {
        var lines = text.Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }

    private static string JsonLower(string text) => Guard.JsonView.AsciiLower(text);

    [GeneratedRegex("[\\u0000-\\u001f\\u007f-\\u009f]", RegexOptions.CultureInvariant)]
    private static partial Regex Controls();

    [GeneratedRegex("\\Are:", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ReplySubject();

    [GeneratedRegex("([A-Za-z][A-Za-z0-9+.-]*://|www\\.)", RegexOptions.CultureInvariant)]
    private static partial Regex Url();

    [GeneratedRegex("[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex Email();

    [GeneratedRegex("\\A(_{8,}|-{3,} ?Original Message ?-{3,})\\s*\\z", RegexOptions.CultureInvariant)]
    private static partial Regex QuoteSeparator();

    private sealed record DraftRecord(JsonElement Id, string Kind, string Subject, List<string> Recipients, string Reason, string Conversation);
}
