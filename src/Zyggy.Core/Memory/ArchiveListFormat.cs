using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Zyggy.Core.Memory;

/// <summary>Whether a fact line names an archived item, or one of its two files is missing.</summary>
internal enum ArchiveIndexState
{
    /// <summary>Some <c>.md</c> file under <c>private/</c> or <c>business/</c> contains the item's path.</summary>
    Indexed,

    /// <summary>No side file names the item yet.</summary>
    Unindexed,

    /// <summary>An item without a sidecar, or a sidecar without an item.</summary>
    Orphan,
}

/// <summary>One row of <c>archive list</c>; paths relative to the principal directory, <see langword="null"/> where unknown.</summary>
internal sealed record ArchiveListRow(
    string? ItemPath,
    string? SidecarPath,
    Slug Project,
    Slug Slug,
    ArchiveMediaType? Type,
    long? Size,
    string? Name,
    string? Description,
    DateOnly? Archived,
    ArchiveIndexState State)
{
    /// <summary>The path the row is sorted and printed by: the item, else the sidecar.</summary>
    public string Path => ItemPath ?? SidecarPath!;
}

/// <summary>
/// The output of <c>archive list</c> (spec 37 AC-18): text rows <c>&lt;path&gt;  &lt;media type&gt;  &lt;size&gt;  &lt;state&gt;  — &lt;description&gt;</c>
/// (<c>-</c> where unknown), or a JSON array with the keys <c>item</c>, <c>sidecar</c>, <c>project</c>, <c>slug</c>, <c>media_type</c>,
/// <c>size_bytes</c>, <c>name</c>, <c>description</c>, <c>archived</c>, <c>state</c> (null where unknown; plan assumption A5).
/// </summary>
internal static class ArchiveListFormat
{
    private static readonly JsonWriterOptions JsonOptions = new()
    {
        Indented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Text(IReadOnlyList<ArchiveListRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var builder = new StringBuilder();
        foreach (var row in rows)
        {
            var type = row.Type is { } t ? ArchiveMediaTypeWire.ToWire(t) : "-";
            var size = row.Size is { } s ? ArchiveSize.Format(s) : "-";
            builder.Append(CultureInfo.InvariantCulture, $"{row.Path}  {type}  {size}  {State(row.State)}  — {row.Description ?? "-"}\n");
        }

        return builder.ToString();
    }

    public static string Json(IReadOnlyList<ArchiveListRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, JsonOptions))
        {
            writer.WriteStartArray();
            foreach (var row in rows)
            {
                writer.WriteStartObject();
                writer.WriteString("item", row.ItemPath);
                writer.WriteString("sidecar", row.SidecarPath);
                writer.WriteString("project", row.Project.Value);
                writer.WriteString("slug", row.Slug.Value);
                writer.WriteString("media_type", row.Type is { } t ? ArchiveMediaTypeWire.ToWire(t) : null);
                if (row.Size is { } size)
                {
                    writer.WriteNumber("size_bytes", size);
                }
                else
                {
                    writer.WriteNull("size_bytes");
                }

                writer.WriteString("name", row.Name);
                writer.WriteString("description", row.Description);
                writer.WriteString("archived", row.Archived?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                writer.WriteString("state", State(row.State));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    private static string State(ArchiveIndexState state) => state switch
    {
        ArchiveIndexState.Indexed => "indexed",
        ArchiveIndexState.Unindexed => "unindexed",
        ArchiveIndexState.Orphan => "orphan",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };
}
