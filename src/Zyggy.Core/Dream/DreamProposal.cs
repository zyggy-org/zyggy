using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zyggy.Core.Dream;

/// <summary>The filing proposal the model returns (Appendix A <c>filing.schema.json</c>).</summary>
internal sealed record DreamProposal(
    IReadOnlyList<Disposition> Dispositions,
    IReadOnlyList<NewCategory> NewCategories,
    IReadOnlyList<CreateFile> Creates,
    IReadOnlyList<EditFile> Edits,
    string Notes);

/// <summary>What happened to one input line.</summary>
internal sealed record Disposition(string Line, string Outcome, string? Target = null, string? DropReason = null);

/// <summary>A category to create on a side.</summary>
internal sealed record NewCategory(string Side, string Name, string Description);

/// <summary>A new memory file.</summary>
internal sealed record CreateFile(string Path, string Name, string Description, IReadOnlyList<string> Aliases, IReadOnlyList<string> Lines);

/// <summary>Changes to an existing memory file.</summary>
internal sealed record EditFile(
    string Path,
    IReadOnlyList<string> Append,
    IReadOnlyList<LineReplace> Replace,
    IReadOnlyList<LineRemove> Remove,
    string? Description = null,
    IReadOnlyList<string>? Aliases = null);

/// <summary>Replace an exact existing body line.</summary>
internal sealed record LineReplace(string Old, string New);

/// <summary>Remove an exact existing body line.</summary>
internal sealed record LineRemove(string Old, string Reason);

/// <summary>The compression proposal (Appendix A <c>compression.schema.json</c>).</summary>
internal sealed record CompressionProposal(string Path, IReadOnlyList<string> Lines, IReadOnlyList<CompressionRemoval> Removed, string? Description = null);

/// <summary>A line a compression removed.</summary>
internal sealed record CompressionRemoval(string Old, string Reason, string? Into = null);

/// <summary>The migration proposal (Appendix A <c>migration.schema.json</c>).</summary>
internal sealed record MigrationProposal(IReadOnlyList<MigrationMove> Moves, IReadOnlyList<NewCategory> NewCategories);

/// <summary>One legacy file and where it goes.</summary>
internal sealed record MigrationMove(string From, string To);

/// <summary>Strict, source-generated deserialisation of the model's structured output: unknown or missing members fail.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    RespectRequiredConstructorParameters = true,
    RespectNullableAnnotations = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(DreamProposal))]
[JsonSerializable(typeof(CompressionProposal))]
[JsonSerializable(typeof(MigrationProposal))]
internal sealed partial class DreamJsonContext : JsonSerializerContext;

/// <summary>Turns structured output into a proposal, or <see langword="null"/> when it does not deserialise.</summary>
internal static class DreamProposalParser
{
    public static DreamProposal? Parse(JsonElement? output) => Deserialize(output, e => e.Deserialize(DreamJsonContext.Default.DreamProposal));

    public static CompressionProposal? ParseCompression(JsonElement? output) =>
        Deserialize(output, e => e.Deserialize(DreamJsonContext.Default.CompressionProposal));

    public static MigrationProposal? ParseMigration(JsonElement? output) =>
        Deserialize(output, e => e.Deserialize(DreamJsonContext.Default.MigrationProposal));

    private static T? Deserialize<T>(JsonElement? output, Func<JsonElement, T?> read)
        where T : class
    {
        if (output is not { ValueKind: JsonValueKind.Object } element)
        {
            return null;
        }

        try
        {
            return read(element);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }
}
