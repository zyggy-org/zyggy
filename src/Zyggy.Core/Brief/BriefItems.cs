using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

using Zyggy.Core.M365.Graph;

namespace Zyggy.Core.Brief;

/// <summary>Where a Z item's target is now, against where the item expects it.</summary>
internal enum ZItemStatus
{
    Ok,
    Moved,
    Deleted,
    Unknown,
}

/// <summary>
/// <c>zyggy brief items</c>'s core (spec 35 AC-24): resolves the selected numbers from the item list only and checks each target with one
/// Graph read (after one read of the folder list) — still in its expected folder (Inbox for <c>move</c>, Drafts for <c>send</c> and
/// <c>discard-draft</c>) is <c>ok</c>, in Deleted Items or gone is <c>deleted</c>, elsewhere is <c>moved</c>; a number not in the list is
/// <c>unknown</c>. The "file the other mails" item becomes one line per mail with its own status. Any Graph failure returns no line.
/// </summary>
internal static class BriefItems
{
    private static readonly JsonWriterOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static async Task<(IReadOnlyList<string> Lines, GraphFailure? Failure)> ResolveAsync(
        BriefSidecar sidecar, ZSelection selection, IGraphReader reader, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sidecar);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(reader);
        var numbers = selection.All ? sidecar.Items.Select(i => i.N).ToList() : selection.Numbers;
        var byNumber = sidecar.Items.GroupBy(i => i.N).ToDictionary(g => g.Key, g => g.First());
        var lines = new List<string>();
        Folders? folders = null;
        foreach (var n in numbers)
        {
            if (!byNumber.TryGetValue(n, out var item))
            {
                lines.Add(Line(w => w.WriteNumber("n", n), "unknown"));
                continue;
            }

            if (folders is null)
            {
                var list = await reader.MailFoldersAsync(cancellationToken).ConfigureAwait(false);
                if (list.Failure is { } listFailure)
                {
                    return ([], listFailure);
                }

                folders = new Folders(Id(list.Value!, "inbox"), Id(list.Value!, "drafts"), Id(list.Value!, "deleteditems"));
            }

            if (item.Kind == "file-other")
            {
                foreach (var id in item.MessageIds ?? [])
                {
                    var location = await reader.MessageLocationAsync(id, cancellationToken).ConfigureAwait(false);
                    if (location.Failure is { } failure)
                    {
                        return ([], failure);
                    }

                    var at = location.Value!;
                    lines.Add(Line(
                        w =>
                        {
                            w.WriteNumber("n", item.N);
                            w.WriteString("kind", "move");
                            w.WriteString("group", "file-other");
                            w.WriteString("messageId", id);
                            w.WriteString("destination", item.Destination ?? "archive");
                            Optional(w, "subject", at.Subject);
                            Optional(w, "senderName", at.SenderName);
                            Optional(w, "received", at.Received);
                        },
                        Status(at, folders.Inbox, folders.DeletedItems)));
                }

                continue;
            }

            var (target, expected) = item.Kind == "move" ? (item.MessageId, folders.Inbox) : (item.DraftId, folders.Drafts);
            if (target is null)
            {
                lines.Add(Item(item, "unknown"));
                continue;
            }

            var read = await reader.MessageLocationAsync(target, cancellationToken).ConfigureAwait(false);
            if (read.Failure is { } readFailure)
            {
                return ([], readFailure);
            }

            lines.Add(Item(item, Status(read.Value!, expected, folders.DeletedItems)));
        }

        return (lines, null);
    }

    private static string Status(MessageLocation at, string? expected, string? deletedItems) =>
        (!at.Present || (deletedItems is not null && at.ParentFolderId == deletedItems) ? ZItemStatus.Deleted
            : expected is not null && at.ParentFolderId == expected ? ZItemStatus.Ok
            : ZItemStatus.Moved).ToString().ToLowerInvariant();

    private static string? Id(IReadOnlyList<MailFolder> folders, string wellKnown) => folders.FirstOrDefault(f => f.WellKnownName == wellKnown)?.Id;

    // The item list's fields, in its order, then the status.
    private static string Item(SidecarItem item, string status) => Line(
        w =>
        {
            w.WriteNumber("n", item.N);
            w.WriteString("kind", item.Kind);
            Optional(w, "messageId", item.MessageId);
            Optional(w, "draftId", item.DraftId);
            Optional(w, "destination", item.Destination);
            w.WriteString("subject", item.Subject);
            if (item.Sender is { } sender)
            {
                w.WriteStartObject("sender");
                w.WriteString("name", sender.Name);
                w.WriteString("address", sender.Address);
                w.WriteEndObject();
            }

            Optional(w, "received", item.Received);
        },
        status);

    private static void Optional(Utf8JsonWriter w, string name, string? value)
    {
        if (value is not null)
        {
            w.WriteString(name, value);
        }
    }

    private static string Line(Action<Utf8JsonWriter> fields, string status)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, Compact))
        {
            writer.WriteStartObject();
            fields(writer);
            writer.WriteString("status", status);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private sealed record Folders(string? Inbox, string? Drafts, string? DeletedItems);
}
