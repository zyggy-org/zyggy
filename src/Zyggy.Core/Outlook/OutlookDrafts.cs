using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Zyggy.Core.Memory;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Outlook;

/// <summary>
/// One reply draft of a work mail brief (spec 58 slice 3, D-Q5, D-Q10, D-Q17): kept on the work laptop only, never saved in Outlook.
/// It answers the sender of the mail it replies to; <paramref name="Text"/> is what the agent wrote (no link, no address);
/// <paramref name="Quoted"/> is the history as received, below it.
/// </summary>
/// <param name="Number">The draft's number in the last brief (1, 2, …), by which an attended session sends it.</param>
/// <param name="Reference">The item reference of the mail it answers.</param>
/// <param name="ToName">The sender's display name.</param>
/// <param name="To">The sender's address: the one recipient.</param>
/// <param name="Subject">The reply subject.</param>
/// <param name="Text">The text the agent wrote.</param>
/// <param name="Quoted">The quoted plain-text history.</param>
internal sealed record OutlookDraft(int Number, string Reference, string ToName, string To, string Subject, string Text, string Quoted);

/// <summary>
/// Older mail a capped brief left out: received at or after <paramref name="Since"/> and before <paramref name="Before"/> (the oldest
/// mail that brief showed). The next brief takes it first, before any mail after the recorded brief.
/// </summary>
/// <param name="Since">The capped brief's lower bound.</param>
/// <param name="Before">The received time of the oldest mail it showed (exclusive).</param>
internal sealed record OutlookBacklog(DateTimeOffset Since, DateTimeOffset Before);

/// <summary>What was briefed and the drafts of the last brief (spec 58 "What was briefed is remembered on the laptop").</summary>
/// <param name="BriefedUntil">The received time of the newest briefed mail; the next brief starts after it.</param>
/// <param name="Drafts">The drafts of the last brief.</param>
/// <param name="Backlog">Older mail a capped brief left out, or <see langword="null"/>.</param>
internal sealed record OutlookBriefState(DateTimeOffset? BriefedUntil, IReadOnlyList<OutlookDraft> Drafts, OutlookBacklog? Backlog = null)
{
    /// <summary>Gets the state before any brief.</summary>
    public static OutlookBriefState None { get; } = new(null, []);
}

/// <summary>
/// What the last <c>zyggy outlook brief</c> showed, so <c>brief-done</c> records only what was briefed: its window, the newest and oldest
/// mail it showed, whether it was capped, and whether it was a backlog brief. Kept in <c>offer.json</c> beside the brief state; it is
/// not the recorded brief.
/// </summary>
/// <param name="Since">The brief's lower bound.</param>
/// <param name="Until">The brief's <c>until</c> (the newest mail shown, or the since when none).</param>
/// <param name="Oldest">The oldest mail shown, or <see langword="null"/> when none.</param>
/// <param name="More">Whether the cap was reached.</param>
/// <param name="FromBacklog">Whether the brief read a backlog window.</param>
internal sealed record OutlookBriefOffer(DateTimeOffset Since, DateTimeOffset Until, DateTimeOffset? Oldest, bool More, bool FromBacklog);

/// <summary>
/// The rules of a reply draft and the laptop-local brief state (spec 58 slice 3): the reply subject, the written-text check (the
/// text the agent writes holds no link and no address; the quoted history is reproduced as received), the quoted history, and
/// <c>&lt;nodeDir&gt;/work/&lt;tenant&gt;/outlook/brief.json</c>, written atomically. Nothing here touches Outlook.
/// </summary>
internal static partial class OutlookDrafts
{
    /// <summary>The state file's name.</summary>
    public const string StateFileName = "brief.json";

    private static readonly JsonWriterOptions Indented = new() { Indented = true, NewLine = "\n" };

    /// <summary>Returns the path of the brief state of a tenant on this machine.</summary>
    /// <param name="nodeDir">The node directory.</param>
    /// <param name="tenant">The tenant.</param>
    /// <returns>The path.</returns>
    public static string StatePath(string nodeDir, TenantId tenant)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeDir);
        ArgumentNullException.ThrowIfNull(tenant);
        return Path.Join(nodeDir, "work", tenant.Value, "outlook", StateFileName);
    }

    /// <summary>Returns the subject of a reply: <c>RE: </c> before the subject, unless it already starts with <c>RE:</c> (any case).</summary>
    /// <param name="subject">The subject of the mail answered.</param>
    /// <returns>The reply subject.</returns>
    public static string ReplySubject(string subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        return subject.StartsWith("RE:", StringComparison.OrdinalIgnoreCase) ? subject : "RE: " + subject;
    }

    /// <summary>Checks the text the agent wrote for a draft: it must not be blank and holds no link and no address (spec 58 "The work mail brief").</summary>
    /// <param name="text">The written text.</param>
    /// <returns><see langword="null"/> when allowed, else <c>has no text</c>, <c>holds a link</c> or <c>holds an address</c>.</returns>
    public static string? WrittenTextProblem(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (string.IsNullOrWhiteSpace(text))
        {
            return "has no text";
        }

        if (Link().IsMatch(text))
        {
            return "holds a link";
        }

        return Address().IsMatch(text) ? "holds an address" : null;
    }

    /// <summary>Builds the quoted history below a reply: a header of the mail answered and its plain-text body as read (D-Q10).</summary>
    /// <param name="mail">The mail answered, as the reader read it.</param>
    /// <returns>The quoted history.</returns>
    public static string Quote(OutlookMail mail)
    {
        ArgumentNullException.ThrowIfNull(mail);
        var summary = mail.Summary;
        var text = new StringBuilder()
            .Append("-----Original Message-----\n")
            .Append("From: ").Append(summary.SenderName).Append(" <").Append(summary.SenderAddress).Append(">\n")
            .Append("Sent: ").Append(summary.Received).Append('\n')
            .Append("Subject: ").Append(summary.Subject).Append("\n\n")
            .Append(mail.Body ?? "(protected: body not read)");
        if (mail.BodyTruncated)
        {
            text.Append("\n[… the rest of the quoted history was cut at the read length]");
        }

        return text.ToString();
    }

    /// <summary>Reads the brief state; <see cref="OutlookBriefState.None"/> when there is none or it cannot be read.</summary>
    /// <param name="path">The state file.</param>
    /// <returns>The state.</returns>
    public static OutlookBriefState Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path))
        {
            return OutlookBriefState.None;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            DateTimeOffset? until = Date(root, "briefedUntil");
            OutlookBacklog? backlog = root.TryGetProperty("backlog", out var b) && b.ValueKind == JsonValueKind.Object
                && Date(b, "since") is { } backlogSince && Date(b, "before") is { } backlogBefore
                ? new OutlookBacklog(backlogSince, backlogBefore)
                : null;
            var drafts = new List<OutlookDraft>();
            if (root.TryGetProperty("drafts", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in list.EnumerateArray())
                {
                    drafts.Add(new OutlookDraft(
                        d.GetProperty("number").GetInt32(),
                        d.GetProperty("reference").GetString()!,
                        d.GetProperty("toName").GetString()!,
                        d.GetProperty("to").GetString()!,
                        d.GetProperty("subject").GetString()!,
                        d.GetProperty("text").GetString()!,
                        d.GetProperty("quoted").GetString()!));
                }
            }

            return new OutlookBriefState(until, drafts, backlog);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            // A damaged state file is a fresh start, never a crash: the next brief reads the default window.
            return OutlookBriefState.None;
        }
    }

    /// <summary>Returns the path of the last brief's offer, beside the brief state.</summary>
    /// <param name="statePath">The brief state file.</param>
    /// <returns>The path.</returns>
    public static string OfferPath(string statePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(statePath);
        return Path.Join(Path.GetDirectoryName(statePath), "offer.json");
    }

    /// <summary>Reads the last brief's offer; <see langword="null"/> when there is none or it cannot be read.</summary>
    /// <param name="path">The offer file.</param>
    /// <returns>The offer.</returns>
    public static OutlookBriefOffer? LoadOffer(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            return Date(root, "since") is { } since && Date(root, "until") is { } until
                ? new OutlookBriefOffer(since, until, Date(root, "oldest"), root.GetProperty("more").GetBoolean(), root.GetProperty("fromBacklog").GetBoolean())
                : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Writes the last brief's offer atomically.</summary>
    /// <param name="path">The offer file.</param>
    /// <param name="offer">The offer.</param>
    public static void SaveOffer(string path, OutlookBriefOffer offer)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(offer);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, Indented))
        {
            writer.WriteStartObject();
            writer.WriteString("since", Text(offer.Since));
            writer.WriteString("until", Text(offer.Until));
            if (offer.Oldest is { } oldest)
            {
                writer.WriteString("oldest", Text(oldest));
            }
            else
            {
                writer.WriteNull("oldest");
            }

            writer.WriteBoolean("more", offer.More);
            writer.WriteBoolean("fromBacklog", offer.FromBacklog);
            writer.WriteEndObject();
        }

        MemoryFileWriter.WriteAtomically(path, Encoding.UTF8.GetString(buffer.ToArray()) + "\n");
    }

    /// <summary>
    /// The state <c>brief-done --until</c> records (verify 01M81CXC5D9VKYW16HQ734DWAS): when the last brief's offer has that
    /// <c>until</c>, only what it showed counts as briefed — a capped brief leaves its older mail as the backlog, a backlog brief
    /// never moves the recorded brief back and narrows or clears the backlog, and a brief that did not read the backlog (an explicit
    /// <c>--since</c>) never deletes it. Without a matching offer the given until is recorded and any backlog kept.
    /// </summary>
    /// <param name="state">The state before.</param>
    /// <param name="offer">The last brief's offer, or <see langword="null"/>.</param>
    /// <param name="until">The <c>--until</c> given.</param>
    /// <param name="drafts">The new drafts.</param>
    /// <returns>The state to save.</returns>
    public static OutlookBriefState Record(OutlookBriefState state, OutlookBriefOffer? offer, DateTimeOffset until, IReadOnlyList<OutlookDraft> drafts)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(drafts);
        if (offer is null || offer.Until != until)
        {
            return new OutlookBriefState(until, drafts, state.Backlog);
        }

        OutlookBacklog? backlog = offer.More && offer.Oldest is { } oldest ? new OutlookBacklog(offer.Since, oldest) : null;

        // A brief that did not read the backlog (an explicit --since) leaves a waiting backlog in place (verify 01M81EVW8T5BWSRAQ7Z03KPPX3);
        // when it was capped too, the two windows become one that covers both: mail may be briefed again, never skipped.
        if (!offer.FromBacklog && state.Backlog is { } waiting)
        {
            backlog = backlog is null
                ? waiting
                : new OutlookBacklog(Min(waiting.Since, backlog.Since), Max(waiting.Before, backlog.Before));
        }

        DateTimeOffset recorded = offer.FromBacklog && state.BriefedUntil is { } before && before > until ? before : until;
        return new OutlookBriefState(recorded, drafts, backlog);
    }

    private static DateTimeOffset? Date(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParseExact(value.GetString(), OutlookDates.Format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a >= b ? a : b;

    private static string Text(DateTimeOffset value) => value.ToString(OutlookDates.Format, CultureInfo.InvariantCulture);

    /// <summary>Writes the brief state atomically, replacing the last brief's drafts.</summary>
    /// <param name="path">The state file.</param>
    /// <param name="state">The state.</param>
    public static void Save(string path, OutlookBriefState state)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(state);
        MemoryFileWriter.WriteAtomically(path, Write(state));
    }

    /// <summary>Writes the state (or one draft of it) as the JSON both the state file and <c>zyggy outlook drafts</c> use.</summary>
    /// <param name="state">The state.</param>
    /// <param name="only">Only this draft number, or <see langword="null"/> for all.</param>
    /// <returns>The JSON text, ending in a newline.</returns>
    public static string Write(OutlookBriefState state, int? only = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, Indented))
        {
            writer.WriteStartObject();
            if (state.BriefedUntil is { } until)
            {
                writer.WriteString("briefedUntil", until.ToString(OutlookDates.Format, CultureInfo.InvariantCulture));
            }
            else
            {
                writer.WriteNull("briefedUntil");
            }

            if (state.Backlog is { } backlog && only is null)
            {
                writer.WriteStartObject("backlog");
                writer.WriteString("since", Text(backlog.Since));
                writer.WriteString("before", Text(backlog.Before));
                writer.WriteEndObject();
            }

            writer.WriteStartArray("drafts");
            foreach (var draft in state.Drafts.Where(d => only is null || d.Number == only))
            {
                writer.WriteStartObject();
                writer.WriteNumber("number", draft.Number);
                writer.WriteString("reference", draft.Reference);
                writer.WriteString("toName", draft.ToName);
                writer.WriteString("to", draft.To);
                writer.WriteString("subject", draft.Subject);
                writer.WriteString("text", draft.Text);
                writer.WriteString("quoted", draft.Quoted);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    // A scheme link, mailto:, a bare www. host, or a bare domain with a path (example.com/x): a dotted host ending in a top-level
    // label of two or more letters, directly followed by a slash.
    [GeneratedRegex(@"(?i)\b(?:[a-z][a-z0-9+.-]*://|mailto:|www\.|(?:[a-z0-9-]+\.)+[a-z]{2,}/)", RegexOptions.CultureInvariant)]
    private static partial Regex Link();

    // Anything shaped like an e-mail address.
    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+", RegexOptions.CultureInvariant)]
    private static partial Regex Address();
}
