using System.Text.Encodings.Web;
using System.Text.Json;

namespace Zyggy.Core.M365.Graph;

/// <summary>A failed Graph read: the exit code (5 refused, 6 Graph or identity, 3 configuration or key) and the shell's message.</summary>
internal sealed record GraphFailure(int ExitCode, string Message);

/// <summary>A Graph read's value or its failure. Never thrown to the verbs.</summary>
internal sealed record GraphRead<T>(T? Value, GraphFailure? Failure)
{
    public static GraphRead<T> Ok(T value) => new(value, null);

    public static GraphRead<T> Fail(GraphFailure failure) => new(default, failure);
}

/// <summary>A mail folder: <c>{id, displayName, wellKnownName, totalItemCount, excluded}</c>.</summary>
internal sealed record MailFolder(string Id, string? DisplayName, string? WellKnownName, long TotalItemCount, bool Excluded);

/// <summary>A drive: <c>{id, name, site}</c>, site <c>onedrive</c> or the granted site id.</summary>
internal sealed record GraphDrive(string Id, string? Name, string Site);

/// <summary>One file of a drive: <c>{id, path, size, modified}</c>, modified cut to seconds with <c>Z</c>.</summary>
internal sealed record DriveFile(string Id, string Path, long Size, string Modified)
{
    private static readonly JsonWriterOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The shell's JSON line (<c>jq -c</c>), key order id, path, size, modified.</summary>
    public static string ToJsonLine(DriveFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, Compact))
        {
            writer.WriteStartObject();
            writer.WriteString("id", file.Id);
            writer.WriteString("path", file.Path);
            writer.WriteNumber("size", file.Size);
            writer.WriteString("modified", file.Modified);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }
}

/// <summary>A message's sender, reply-to addresses (lower-cased) and conversation.</summary>
internal sealed record MessageSender(string From, IReadOnlyList<string> ReplyTo, string ConversationId)
{
    private static readonly JsonWriterOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The shell's JSON (<c>{from, replyTo, conversationId}</c>).</summary>
    public static string ToJson(MessageSender sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, Compact))
        {
            writer.WriteStartObject();
            writer.WriteString("from", sender.From);
            writer.WriteStartArray("replyTo");
            foreach (var address in sender.ReplyTo)
            {
                writer.WriteStringValue(address);
            }

            writer.WriteEndArray();
            writer.WriteString("conversationId", sender.ConversationId);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }
}

/// <summary>Whether a name exists in a drive folder.</summary>
internal enum ItemPresence
{
    Exists,
    Absent,
}

/// <summary>What a drive item is.</summary>
internal enum ItemKind
{
    Folder,
    File,
    Absent,
}

/// <summary>An Inbox message as the brief's pre-pass lists it (spec 35 Step 4): names only reach the brief; the address stays in the sidecar.</summary>
internal sealed record InboxMessage(string Id, string Subject, string SenderName, string SenderAddress, string Received, string ConversationId, bool HasAttachments);

/// <summary>A sent mail of a conversation: enough to know that, and when, the owner answered.</summary>
internal sealed record SentMarker(string ConversationId, string Sent);

/// <summary>Where a message is now; <see cref="Present"/> false for a 404.</summary>
internal sealed record MessageLocation(bool Present, string? Id, string? ParentFolderId, string? ConversationId, string? Subject, string? SenderName, string? Received)
{
    public static MessageLocation Absent { get; } = new(false, null, null, null, null, null, null);
}
