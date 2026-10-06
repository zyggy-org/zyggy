using System.Text.Json;

using Zyggy.Core.Brief;

namespace Zyggy.Core.M365.Runs;

/// <summary>
/// The <c>mail.json</c> the mail run reads from its run directory (spec 35 Step 4): the pre-pass result as data — ids, local times,
/// sender names (never an address), subjects and whether the owner already answered. Fence tags and control characters in a subject or
/// a name are neutralised as the printed brief neutralises them.
/// </summary>
internal static class MailInput
{
    public static byte[] Render(PrepassResult result, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(zone);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString("watermark", result.Watermark);
            writer.WriteStartArray("mail");
            foreach (var m in result.Mail)
            {
                writer.WriteStartObject();
                writer.WriteString("id", m.Message.Id);
                writer.WriteString("received", LocalTime(m.Message.Received, zone));
                writer.WriteString("senderName", BriefPayload.Neutralise(m.Message.SenderName));
                writer.WriteString("subject", BriefPayload.Neutralise(m.Message.Subject));
                writer.WriteString("conversationId", m.Message.ConversationId);
                writer.WriteBoolean("hasAttachments", m.Message.HasAttachments);
                if (m.Answered is { } answered)
                {
                    writer.WriteString("answered", answered);
                }
                else
                {
                    writer.WriteNull("answered");
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("discard");
            foreach (var d in result.Discard)
            {
                writer.WriteStartObject();
                writer.WriteString("draftId", d.DraftId);
                writer.WriteString("subject", BriefPayload.Neutralise(d.Subject));
                writer.WriteString("answered", d.Answered);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }

    private static string LocalTime(string iso, TimeZoneInfo zone) =>
        DateTimeOffset.TryParse(iso, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var at)
            ? TimeZoneInfo.ConvertTime(at, zone).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)
            : string.Empty;
}
