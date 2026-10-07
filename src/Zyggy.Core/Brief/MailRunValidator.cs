using System.Globalization;
using System.Text.RegularExpressions;

using Zyggy.Core.M365;
using Zyggy.Core.M365.Graph;
using Zyggy.Core.Memory;

namespace Zyggy.Core.Brief;

/// <summary>What the validator needs besides the two inputs.</summary>
internal sealed record ValidationContext(
    DateOnly Date,
    DateTimeOffset Generated,
    string InboxFolderId,
    string? DraftsFolderId,
    string OwnerName,
    IReadOnlyDictionary<string, MessageLocation> Locations,
    SecretPatterns Secrets,
    int SuggestionCap,
    TimeZoneInfo Zone);

/// <summary>
/// Checks the mail run's answer against the pre-pass and the facts before anything is written (spec 35 Step 5, AC-14..AC-19, AC-21, AC-28,
/// AC-64..AC-66): one decision per mail, Z targets that exist where expected, no reply to an answered mail, pay only with an amount, model
/// text withheld when it carries a link, an address, a secret or a contact detail; then the classes the facts demand, the "other" mails as
/// one filing item, the files worth a line, and the Z numbers. Pure.
/// </summary>
internal static partial class MailRunValidator
{
    /// <summary>
    /// AC-20: the AC-18/AC-19 reasons (the model's Z items and text) join the audit verdict; the suggestion-cap note and a withheld
    /// Graph-taken sender name or subject are information, not violations.
    /// </summary>
    public static bool IsViolation(string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        // The sender name and the subject come from Graph, not from the model: withheld the same way, but not a violation (a sender
        // without a display name has its address as its name).
        return !reason.EndsWith(" left to the owner", StringComparison.Ordinal)
            && !reason.EndsWith(" withheld in sender name", StringComparison.Ordinal)
            && !reason.EndsWith(" withheld in subject", StringComparison.Ordinal);
    }

    public static (BriefDocument? Document, string? Rejection) Validate(MailRunOutput output, PrepassResult prepass, ValidationContext context, string watermark)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(prepass);
        ArgumentNullException.ThrowIfNull(context);
        var reasons = new List<string>();
        var byId = output.Mail.GroupBy(m => m.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var unknown in byId.Keys.Where(id => prepass.Mail.All(p => p.Message.Id != id)))
        {
            reasons.Add($"model named an unknown mail {Short(unknown)}");
        }

        var lines = new List<(MailLine Line, int Order)>();
        var candidates = new List<ZCandidate>();
        var you = new List<YouItem>();
        var otherIds = new List<string>();
        var order = 0;
        foreach (var p in prepass.Mail)
        {
            order++;
            var m = p.Message;
            var sender = new ZSender(Clean(m.SenderName, "sender name", reasons, context.Secrets), m.SenderAddress);
            var subject = Cut(Clean(m.Subject, "subject", reasons, context.Secrets), 80);
            var time = Local(m.Received, context.Zone);
            if (!byId.TryGetValue(m.Id, out var e))
            {
                // AC-15: a mail the model omitted is never filed unseen.
                lines.Add((new MailLine(m.Id, MailClass.Important, time, m.Received, sender.Name, subject, "(not summarised)", "nothing"), order));
                continue;
            }

            var cls = e.Class;
            var summary = Cut(Clean(e.Summary, $"summary of {Short(m.Id)}", reasons, context.Secrets), 200);
            var z = e.Action == "z" ? e.Z : null;
            var youProposal = e.Action == "you" ? e.You : null;

            // AC-28: the amount rules.
            string? amountText = null;
            ZProposal? amountMove = null;
            if (e.Amount is { } a)
            {
                var currency = a.Currency is { Length: > 0 } c ? " " + c : string.Empty;
                if (a.Status is "read" or "stated" && a.AmountDue is { } due)
                {
                    if (due > 0)
                    {
                        amountText = $"amount due {MailRunOutput.Money(due)}{currency}" + (a.DueDate is { Length: > 0 } d ? $" by {d}" : string.Empty);
                    }
                    else
                    {
                        amountText = $"amount due {MailRunOutput.Money(0)}{currency} — nothing to pay";
                        if (youProposal is { Kind: "pay" })
                        {
                            youProposal = null;
                        }

                        amountMove = z is null && youProposal is null ? new ZProposal("move", "archive", null, $"amount due {MailRunOutput.Money(0)}{currency}, nothing to pay") : null;
                    }
                }
                else
                {
                    amountText = "amount not read";
                    if (youProposal is { Kind: "pay" } || youProposal is null && z is null)
                    {
                        youProposal = new YouProposal("other", "check the attachment (amount not read)", "the amount is in the attachment");
                    }
                }
            }
            else if (youProposal is { Kind: "pay" })
            {
                youProposal = new YouProposal("other", "check the attachment (amount not read)", "no amount was read");
                amountText = "amount not read";
            }

            z ??= amountMove;

            // AC-64: the classes the facts demand (never lowered below them; an answered mail is at most important).
            if (e.Amount?.DueDate is { Length: > 0 })
            {
                cls = MailClass.Urgent;
            }

            if (p.Answered is not null && cls == MailClass.Urgent)
            {
                cls = MailClass.Important;
            }

            if (cls == MailClass.Other && (youProposal is not null || z is { Kind: "send" } || (z is { Kind: "move" } && z.Destination != "archive")))
            {
                cls = MailClass.Important;
            }

            // The decision.
            string decision;
            if (cls == MailClass.Other)
            {
                otherIds.Add(m.Id);
                decision = "nothing";
                if (z is not null)
                {
                    // A move to Archive of an "other" mail is folded into the file-other item.
                    z = null;
                }
            }
            else if (z is { Kind: "send" })
            {
                if (p.Answered is { } answered)
                {
                    reasons.Add($"reply to the answered mail {Short(m.Id)} dropped");
                    decision = $"nothing (answered {answered})";
                }
                else if (z.DraftId is not { Length: > 0 } draftId
                    || !context.Locations.TryGetValue(draftId, out var draft) || !draft.Present
                    || draft.ParentFolderId != context.DraftsFolderId || draft.ConversationId != m.ConversationId)
                {
                    reasons.Add($"send for {Short(m.Id)} dropped: its draft is not a reply in Drafts");
                    decision = "you";
                    you.Add(new YouItem("reply", sender.Name, subject, Cut(Clean(z.Why, "why", reasons, context.Secrets), 120), cls == MailClass.Urgent));
                }
                else
                {
                    candidates.Add(new ZCandidate(ZKind.Send, m.Id, null, draftId, null, subject, sender, m.Received, Cut(Clean(z.Why, "why", reasons, context.Secrets), 120), cls, order));
                    decision = "Z";
                }
            }
            else if (z is { Kind: "move" })
            {
                var destination = z.Destination == "archive" ? ZDestination.Archive : z.Destination == "deleteditems" ? ZDestination.DeletedItems : (ZDestination?)null;
                if (destination is null || !context.Locations.TryGetValue(m.Id, out var location) || !location.Present || location.ParentFolderId != context.InboxFolderId)
                {
                    reasons.Add($"move of {Short(m.Id)} dropped: not in the Inbox or no valid destination");
                    decision = p.Answered is { } answered ? $"nothing (answered {answered})" : "nothing";
                }
                else
                {
                    candidates.Add(new ZCandidate(ZKind.Move, m.Id, null, null, destination, subject, sender, m.Received, Cut(Clean(z.Why, "why", reasons, context.Secrets), 80), cls, order));
                    decision = "Z";
                }
            }
            else if (youProposal is not null)
            {
                var action = youProposal.Kind == "pay" && amountText is not null && !amountText.Contains("not read", StringComparison.Ordinal)
                    ? "pay " + amountText["amount due ".Length..]
                    : Cut(Clean(youProposal.Action, "action", reasons, context.Secrets), 80);
                you.Add(new YouItem(action, sender.Name, subject, Cut(Clean(youProposal.Why, "why", reasons, context.Secrets), 120), cls == MailClass.Urgent));
                decision = "you";
            }
            else
            {
                decision = p.Answered is { } answered ? $"nothing (answered {answered})" : "nothing";
            }

            if (amountText is not null)
            {
                summary = summary.Length == 0 ? amountText : summary + "; " + amountText;
            }

            lines.Add((new MailLine(m.Id, cls, time, m.Received, sender.Name, subject, summary, decision), order));
        }

        // AC-22: the discard items, after the mails.
        foreach (var d in prepass.Discard)
        {
            order++;
            candidates.Add(new ZCandidate(ZKind.DiscardDraft, null, null, d.DraftId, ZDestination.DeletedItems, Cut(Clean(d.Subject, "subject", reasons, context.Secrets), 80), new ZSender(context.OwnerName, string.Empty), string.Empty, $"answered {d.Answered}", MailClass.Important, order));
        }

        // AC-65: the "other" mails as one filing item.
        if (otherIds.Count > 0)
        {
            candidates.Add(new ZCandidate(ZKind.FileOther, null, otherIds, null, ZDestination.Archive, $"{otherIds.Count} other mails", new ZSender(string.Empty, string.Empty), string.Empty, string.Empty, MailClass.Other, int.MaxValue));
        }

        var (items, dropped) = BriefNumbering.Number(candidates, context.SuggestionCap);
        if (dropped.Count > 0)
        {
            reasons.Add($"{dropped.Count} Z items over suggestion_cap {context.SuggestionCap} left to the owner");
        }

        // Each line's decision: its Z number, or "you" for a candidate the cap dropped.
        var zByMail = items.Where(i => i.Kind is ZKind.Send or ZKind.Move).ToDictionary(i => i.MessageId!, i => i.N, StringComparer.Ordinal);
        var droppedIds = dropped.Where(c => c.MessageId is not null).Select(c => c.MessageId!).ToHashSet(StringComparer.Ordinal);
        var mail = lines
            .OrderByDescending(l => l.Line.Class)
            .ThenBy(l => l.Order)
            .Select(l => l.Line.Decision == "Z"
                ? l.Line with { Decision = zByMail.TryGetValue(l.Line.Id, out var n) ? $"Z{n}" : droppedIds.Contains(l.Line.Id) ? "you" : "nothing" }
                : l.Line)
            .ToList();
        foreach (var c in dropped.Where(c => c.MessageId is not null))
        {
            you.Add(new YouItem(c.Kind == ZKind.Send ? "send the reply draft" : c.Destination == ZDestination.Archive ? "file" : "move to Deleted Items", c.Sender.Name, c.Subject, c.Why, c.Class == MailClass.Urgent));
        }

        // AC-66: a file line only when tied to an urgent/important mail or changed by someone else.
        var lineIds = mail.Where(l => l.Class != MailClass.Other).Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        var files = new List<FileLine>();
        var filesOther = 0;
        foreach (var f in output.Files)
        {
            var kept = (f.TiedTo is { Length: > 0 } tied && lineIds.Contains(tied)) || !string.Equals(f.By, context.OwnerName, StringComparison.Ordinal);
            if (!kept)
            {
                filesOther++;
                continue;
            }

            files.Add(new FileLine(
                Cut(Clean(f.Name, "file name", reasons, context.Secrets), 120),
                Cut(Clean(f.Drive, "drive", reasons, context.Secrets), 40),
                Cut(Clean(f.Folder, "folder", reasons, context.Secrets), 80),
                Local(f.Modified, context.Zone),
                f.Modified,
                Cut(Clean(f.By, "file author", reasons, context.Secrets), 60),
                Cut(Clean(f.About, "about", reasons, context.Secrets), 200),
                f.YouAction is { Length: > 0 } ya ? Cut(Clean(ya, "action", reasons, context.Secrets), 80) : null));
        }

        var counts = new BriefCounts(
            mail.Count(l => l.Class == MailClass.Urgent),
            mail.Count(l => l.Class == MailClass.Important),
            otherIds.Count,
            files.Count,
            filesOther);
        var document = new BriefDocument(context.Date, context.Generated, BriefMode.Weekday, watermark, "ok", reasons, mail, otherIds, files, filesOther, items, you, [], counts);
        return (document, null);
    }

    /// <summary>AC-19: model text with a link, an address, a secret or a contact detail is withheld, never printed.</summary>
    internal static string Clean(string text, string what, List<string> reasons, SecretPatterns secrets)
    {
        ArgumentNullException.ThrowIfNull(reasons);
        ArgumentNullException.ThrowIfNull(secrets);
        var neutral = BriefPayload.Neutralise(text ?? string.Empty).Replace('\n', ' ').Trim();
        if (WithheldReason(neutral, secrets) is not { } reason)
        {
            return neutral;
        }

        reasons.Add($"{reason} withheld in {what}");
        return $"[withheld: {reason}]";
    }

    /// <summary>Why model text may not be printed — a link, an address, a secret pattern or a contact detail — or <see langword="null"/>.</summary>
    internal static string? WithheldReason(string text, SecretPatterns secrets)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(secrets);
        return Url().IsMatch(text) ? "link"
            : Email().IsMatch(text) ? "address"
            : secrets.TryMatch(text, out var name) ? "secret " + name
            : ContactDetailPatterns.Contains(text) ? "contact detail"
            : null;
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";

    private static string Local(string iso, TimeZoneInfo zone) =>
        DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
            ? TimeZoneInfo.ConvertTime(at, zone).ToString("HH:mm", CultureInfo.InvariantCulture)
            : string.Empty;

    private static string Short(string id) => id.Length <= 12 ? id : id[..12] + "…";

    [GeneratedRegex(@"([A-Za-z][A-Za-z0-9+.-]*://|www\.)", RegexOptions.CultureInvariant)]
    private static partial Regex Url();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex Email();
}
