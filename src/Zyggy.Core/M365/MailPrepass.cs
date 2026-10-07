using System.Globalization;
using System.Text.Json;

using Zyggy.Core.Brief;
using Zyggy.Core.M365.Graph;

namespace Zyggy.Core.M365;

/// <summary>A listed Inbox mail with what the pre-pass knows about it: answered at the local <c>HH:MM</c> of the first later sent mail, or not.</summary>
internal sealed record PrepassMail(InboxMessage Message, string? Answered);

/// <summary>An earlier reply draft of Zyggy's that the owner has since answered himself: the brief offers to discard it.</summary>
internal sealed record DiscardItem(string DraftId, string Subject, string Answered);

/// <summary>What the mail run gets as data, and what the renderer needs afterwards.</summary>
internal sealed record PrepassResult(string Watermark, IReadOnlyList<PrepassMail> Mail, IReadOnlyList<DiscardItem> Discard);

/// <summary>
/// The brief's pre-pass (spec 35 Step 4, AC-21, AC-22): the binary itself lists the new Inbox mail, finds in Sent Items which of them the
/// owner already answered and when, and turns Zyggy's own earlier reply drafts to answered mails into discard items. Graph reads only; the
/// result is data for the mail run, never an instruction.
/// </summary>
internal sealed class MailPrepass(IGraphReader reader, M365Paths paths, M365Configuration configuration, BriefSettings settings, TimeZoneInfo zone, TimeProvider clock)
{
    private static readonly StateEntry MailWatermark = new("mail-watermark", "mail-watermark", StateGrammar.Iso);

    public async Task<(PrepassResult? Result, GraphFailure? Failure)> RunAsync(string inboxId, string? draftsFolderId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(inboxId);
        var now = clock.GetUtcNow();
        var watermark = new M365State(paths, clock).Get(MailWatermark)!.TrimEnd('\n');

        var inbox = await reader.InboxSinceAsync(inboxId, watermark, configuration.MailMaxItems, cancellationToken).ConfigureAwait(false);
        if (inbox.Failure is not null)
        {
            return (null, inbox.Failure);
        }

        // One Sent Items query covering the listed mails and the earlier reply drafts (at most brief_keep_days old).
        var keepFrom = now.AddDays(-settings.KeepDays);
        var oldestListed = inbox.Value!.Select(m => Parse(m.Received)).Where(t => t is not null).Min() ?? now;
        var since = oldestListed < keepFrom ? oldestListed : keepFrom;
        var sent = await reader.SentSinceAsync(Iso(since), cancellationToken).ConfigureAwait(false);
        if (sent.Failure is not null)
        {
            return (null, sent.Failure);
        }

        var sentByConversation = sent.Value!
            .Select(s => (s.ConversationId, At: Parse(s.Sent)))
            .Where(s => s.ConversationId.Length > 0 && s.At is not null)
            .GroupBy(s => s.ConversationId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(s => s.At!.Value).Order().ToList(), StringComparer.Ordinal);

        var mail = inbox.Value!.Select(m => new PrepassMail(m, AnsweredAt(sentByConversation, m.ConversationId, Parse(m.Received)))).ToList();

        var discard = new List<DiscardItem>();
        if (draftsFolderId is not null)
        {
            foreach (var draftId in EarlierReplyDraftIds(now))
            {
                var location = await reader.MessageLocationAsync(draftId, cancellationToken).ConfigureAwait(false);
                if (location.Failure is not null)
                {
                    return (null, location.Failure);
                }

                var draft = location.Value!;
                if (!draft.Present || draft.ParentFolderId != draftsFolderId || draft.ConversationId is not { Length: > 0 } conversation)
                {
                    continue;
                }

                if (AnsweredAt(sentByConversation, conversation, Parse(draft.Received ?? string.Empty)) is { } answered)
                {
                    discard.Add(new DiscardItem(draftId, draft.Subject ?? string.Empty, answered));
                }
            }
        }

        return (new PrepassResult(watermark, mail, discard), null);
    }

    // The first sent mail of the conversation after the message: the owner has answered (Assumption 3).
    private string? AnsweredAt(Dictionary<string, List<DateTimeOffset>> sentByConversation, string conversationId, DateTimeOffset? received)
    {
        if (received is null || !sentByConversation.TryGetValue(conversationId, out var sentTimes))
        {
            return null;
        }

        var first = sentTimes.FirstOrDefault(t => t > received);
        return first == default ? null : TimeZoneInfo.ConvertTime(first, zone).ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    // The reply-kind draft ids of the receipts m365/brief-<date>.json of the last brief_keep_days days, in date order.
    private List<string> EarlierReplyDraftIds(DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var ids = new List<string>();
        for (var back = settings.KeepDays; back >= 1; back--)
        {
            var path = paths.StateFile($"brief-{BriefPaths.Iso(today.AddDays(-back))}.json");
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(path));
                if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("drafts", out var drafts) || drafts.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var draft in drafts.EnumerateArray())
                {
                    if (draft.ValueKind == JsonValueKind.Object
                        && draft.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == "reply"
                        && draft.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && M365Grammar.Id().IsMatch(id.GetString()!)
                        && !ids.Contains(id.GetString()!, StringComparer.Ordinal))
                    {
                        ids.Add(id.GetString()!);
                    }
                }
            }
            catch (JsonException)
            {
                // A receipt that is not JSON is not a record.
            }
        }

        return ids;
    }

    private static DateTimeOffset? Parse(string iso) =>
        DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at) ? at : null;

    private static string Iso(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
