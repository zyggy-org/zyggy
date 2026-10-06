using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zyggy.Core.Brief;

/// <summary>The item list beside the brief, <c>brief-&lt;date&gt;.json</c> (spec 35 Contracts "Files"): what the session acts on; addresses live here only.</summary>
internal sealed record BriefSidecar(
    int Schema,
    string Date,
    string Generated,
    string Mode,
    string Watermark,
    string Audit,
    IReadOnlyList<string> AuditReasons,
    SidecarCounts Counts,
    [property: JsonPropertyName("page_exceeded")] bool PageExceeded,
    IReadOnlyList<SidecarItem> Items,
    IReadOnlyList<SidecarIdea> Ideas)
{
    public static BriefSidecar From(BriefDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new BriefSidecar(
            1,
            BriefPaths.Iso(document.Date),
            document.Generated.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
            document.Mode == BriefMode.Weekend ? "weekend" : "weekday",
            document.Watermark,
            document.Audit,
            document.AuditReasons,
            new SidecarCounts(document.Counts.Urgent, document.Counts.Important, document.Counts.Other, document.Counts.Files, document.Counts.FilesOther),
            document.PageExceeded,
            document.Items.Select(i => new SidecarItem(
                i.N,
                i.Kind switch { ZKind.Send => "send", ZKind.Move => "move", ZKind.DiscardDraft => "discard-draft", _ => "file-other" },
                i.MessageId,
                i.MessageIds,
                i.DraftId,
                i.Destination switch { ZDestination.Archive => "archive", ZDestination.DeletedItems => "deleteditems", _ => null },
                i.Subject,
                i.Kind == ZKind.FileOther ? null : new SidecarSender(i.Sender.Name, i.Sender.Address),
                i.Received.Length == 0 ? null : i.Received)).ToList(),
            document.Ideas.Select(i => new SidecarIdea(i.N, i.Id, i.Area)).ToList());
    }

    private static readonly JsonSerializerOptions Options = new(BriefJsonContext.Default.Options) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, NewLine = "\n" };

    public byte[] ToBytes() => [.. JsonSerializer.SerializeToUtf8Bytes(this, Options), (byte)'\n'];

    public static BriefSidecar? Parse(ReadOnlySpan<byte> bytes) => JsonSerializer.Deserialize(bytes, BriefJsonContext.Default.BriefSidecar);
}

internal sealed record SidecarCounts(int Urgent, int Important, int Other, int Files, int FilesOther);

internal sealed record SidecarSender(string Name, string Address);

internal sealed record SidecarItem(int N, string Kind, string? MessageId, IReadOnlyList<string>? MessageIds, string? DraftId, string? Destination, string Subject, SidecarSender? Sender, string? Received);

internal sealed record SidecarIdea(int N, string Id, string Area);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(BriefSidecar))]
internal sealed partial class BriefJsonContext : JsonSerializerContext;
