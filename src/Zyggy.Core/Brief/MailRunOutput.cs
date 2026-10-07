using System.Globalization;
using System.Text.Json;

namespace Zyggy.Core.Brief;

/// <summary>How the model classed a mail (spec 35 OD-6); the binary may raise a class, never lower it below what the facts demand.</summary>
internal enum MailClass
{
    Other,
    Important,
    Urgent,
}

/// <summary>The model's proposal that Zyggy can carry out from the session: a reply to send, or a move.</summary>
internal sealed record ZProposal(string Kind, string? Destination, string? DraftId, string Why);

/// <summary>The model's proposal of something only the owner can do.</summary>
internal sealed record YouProposal(string Kind, string Action, string Why);

/// <summary>What the model read about an amount due.</summary>
internal sealed record Amount(string Status, decimal? AmountDue, string? Currency, string? DueDate);

/// <summary>One mail of the model's answer.</summary>
internal sealed record MailEntry(string Id, MailClass Class, string Summary, string Action, ZProposal? Z, YouProposal? You, Amount? Amount);

/// <summary>One changed file of the model's answer.</summary>
internal sealed record FileEntry(string Name, string Drive, string Folder, string Modified, string By, string About, string? YouAction, string? TiedTo);

/// <summary>The mail run's structured output (spec 35 Contracts), parsed shape-first: anything outside the schema's shape is no output at all.</summary>
internal sealed record MailRunOutput(IReadOnlyList<MailEntry> Mail, IReadOnlyList<FileEntry> Files, int Replies, int Facts)
{
    /// <summary>Parses the structured output; <see langword="null"/> (with the reason) when the shape is wrong.</summary>
    public static (MailRunOutput? Output, string? Rejection) TryParse(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Object } root)
        {
            return (null, "no structured output");
        }

        if (!root.TryGetProperty("mail", out var mail) || mail.ValueKind != JsonValueKind.Array)
        {
            return (null, "mail is not an array");
        }

        var entries = new List<MailEntry>();
        foreach (var m in mail.EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.Object || Text(m, "id") is not { Length: > 0 } id)
            {
                return (null, "a mail entry has no id");
            }

            var cls = Text(m, "class") switch
            {
                "urgent" => MailClass.Urgent,
                "important" => MailClass.Important,
                "other" => MailClass.Other,
                _ => (MailClass?)null,
            };
            if (cls is null)
            {
                return (null, $"mail {Short(id)} has no class");
            }

            var action = Text(m, "action");
            if (action is not ("z" or "you" or "nothing"))
            {
                return (null, $"mail {Short(id)} has no action");
            }

            ZProposal? z = null;
            if (m.TryGetProperty("z", out var zj) && zj.ValueKind == JsonValueKind.Object)
            {
                var kind = Text(zj, "kind");
                if (kind is not ("send" or "move"))
                {
                    return (null, $"mail {Short(id)}: z.kind is not send or move");
                }

                z = new ZProposal(kind, Text(zj, "destination"), Text(zj, "draftId"), Text(zj, "why") ?? string.Empty);
            }

            YouProposal? you = null;
            if (m.TryGetProperty("you", out var yj) && yj.ValueKind == JsonValueKind.Object)
            {
                var kind = Text(yj, "kind");
                if (kind is not ("pay" or "other"))
                {
                    return (null, $"mail {Short(id)}: you.kind is not pay or other");
                }

                you = new YouProposal(kind, Text(yj, "action") ?? string.Empty, Text(yj, "why") ?? string.Empty);
            }

            if ((action == "z" && z is null) || (action == "you" && you is null) || (z is not null && you is not null))
            {
                return (null, $"mail {Short(id)} does not end in exactly one decision");
            }

            Amount? amount = null;
            if (m.TryGetProperty("amount", out var aj) && aj.ValueKind == JsonValueKind.Object)
            {
                var status = Text(aj, "status");
                if (status is not ("read" or "stated" or "not_read"))
                {
                    return (null, $"mail {Short(id)}: amount.status is not read, stated or not_read");
                }

                decimal? due = aj.TryGetProperty("amountDue", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetDecimal(out var dec) && dec >= 0 ? dec : null;
                amount = new Amount(status, due, Text(aj, "currency"), Text(aj, "dueDate"));
            }

            entries.Add(new MailEntry(id, cls.Value, Text(m, "summary") ?? string.Empty, action, z, you, amount));
        }

        var files = new List<FileEntry>();
        if (root.TryGetProperty("files", out var fj))
        {
            if (fj.ValueKind != JsonValueKind.Array)
            {
                return (null, "files is not an array");
            }

            foreach (var f in fj.EnumerateArray())
            {
                if (f.ValueKind != JsonValueKind.Object || Text(f, "name") is not { Length: > 0 } name)
                {
                    return (null, "a file entry has no name");
                }

                files.Add(new FileEntry(name, Text(f, "drive") ?? string.Empty, Text(f, "folder") ?? string.Empty, Text(f, "modified") ?? string.Empty,
                    Text(f, "by") ?? string.Empty, Text(f, "about") ?? string.Empty, Text(f, "youAction"), Text(f, "tiedTo")));
            }
        }

        return (new MailRunOutput(entries, files, Int(root, "replies"), Int(root, "facts")), null);
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) && i >= 0 ? i : 0;

    private static string Short(string id) => id.Length <= 12 ? id : id[..12] + "…";

    internal static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}
