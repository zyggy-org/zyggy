using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zyggy.Core.Dream;

/// <summary>
/// One line of <c>dream-runs.jsonl</c> (spec 28 Run record): what a run did, in counts and closed codes only. It never contains a
/// fact text.
/// </summary>
public sealed record DreamRunRecord
{
    /// <summary>Gets the run id (ULID).</summary>
    public required string Run { get; init; }

    /// <summary>Gets the trigger: <c>nightly</c>, <c>on-demand</c> or <c>manual</c>.</summary>
    public required string Trigger { get; init; }

    /// <summary>Gets the <c>zyggy</c> version that ran.</summary>
    public required string Version { get; init; }

    /// <summary>Gets the start time (UTC).</summary>
    public required DateTimeOffset Started { get; init; }

    /// <summary>Gets the end time (UTC).</summary>
    public DateTimeOffset? Ended { get; init; }

    /// <summary>Gets the outcome wire string (<see cref="DreamRunOutcomeWire"/>).</summary>
    public required string Outcome { get; init; }

    /// <summary>Gets the §9 failure reason when the run failed.</summary>
    public string? Reason { get; init; }

    /// <summary>Gets the failure detail token when the run failed.</summary>
    public string? Detail { get; init; }

    /// <summary>Gets the check that aborted the run (<see cref="DreamCheckWire"/>).</summary>
    public string? Check { get; init; }

    /// <summary>Gets one entry per filing call.</summary>
    public IReadOnlyList<DreamBatchRecord> Batches { get; init; } = [];

    /// <summary>Gets one entry per compression call.</summary>
    public IReadOnlyList<DreamCompressionRecord> Compressions { get; init; } = [];

    /// <summary>Gets how many legacy files the one-time layout migration moved (0 for a normal run).</summary>
    public int Migrated { get; init; }

    /// <summary>Gets how many lines were quarantined.</summary>
    public int Quarantined { get; init; }

    /// <summary>Gets the rollup counts.</summary>
    public DreamRollupRecord Rollup { get; init; } = new(0, 0);

    /// <summary>Gets the files withheld from the commit because of a secret pattern.</summary>
    public IReadOnlyList<string> Withheld { get; init; } = [];

    /// <summary>Gets the durable files someone else changed that the run committed as found.</summary>
    public IReadOnlyList<string> Carried { get; init; } = [];

    /// <summary>Gets what is left in the backlog.</summary>
    public DreamInboxRemaining InboxRemaining { get; init; } = new(0, 0);

    /// <summary>Gets the commit SHA, when a commit was made.</summary>
    public string? Commit { get; init; }

    /// <summary>Gets whether the commit reached the remote.</summary>
    public bool Pushed { get; init; }

    /// <summary>Gets the sum of the calls' <c>total_cost_usd</c>.</summary>
    public decimal CostUsdTotal { get; init; }
}

/// <summary>One filing call.</summary>
/// <param name="Lines">Lines offered.</param>
/// <param name="Filed">Lines filed.</param>
/// <param name="Merged">Lines merged.</param>
/// <param name="Duplicate">Lines that were already known.</param>
/// <param name="Dropped">Dropped lines by reason.</param>
/// <param name="FilesCreated">Files created.</param>
/// <param name="FilesEdited">Files edited.</param>
/// <param name="CategoriesCreated">Categories created.</param>
/// <param name="CostUsd">The call's cost estimate.</param>
/// <param name="Turns">The call's turns.</param>
/// <param name="DurationMs">The call's duration.</param>
/// <param name="Result"><c>accepted</c>, <c>aborted:&lt;check&gt;</c> or <c>failed:&lt;reason&gt;:&lt;detail&gt;</c>.</param>
public sealed record DreamBatchRecord(
    int Lines,
    int Filed,
    int Merged,
    int Duplicate,
    IReadOnlyDictionary<string, int> Dropped,
    int FilesCreated,
    int FilesEdited,
    int CategoriesCreated,
    decimal? CostUsd,
    int? Turns,
    long DurationMs,
    string Result)
{
    /// <summary>Gets the fact-free detail of a refused batch (file, edit, line id, counts or pattern name), when aborted.</summary>
    public string? Detail { get; init; }
}

/// <summary>One compression call.</summary>
/// <param name="Path">The compressed file (a path, never its content).</param>
/// <param name="Result"><c>accepted</c>, <c>rejected</c> or <c>failed:&lt;reason&gt;</c>.</param>
/// <param name="CostUsd">The call's cost estimate.</param>
public sealed record DreamCompressionRecord(string Path, string Result, decimal? CostUsd);

/// <summary>Rollup counts.</summary>
/// <param name="DailyRolled">Daily files rolled into their month.</param>
/// <param name="InboxDeleted">Closed inbox files deleted.</param>
public sealed record DreamRollupRecord(int DailyRolled, int InboxDeleted);

/// <summary>The backlog after the run.</summary>
/// <param name="Files">Inbox and daily files with unconsumed lines.</param>
/// <param name="Lines">Unconsumed lines.</param>
public sealed record DreamInboxRemaining(int Files, int Lines);

/// <summary>Reads and appends <c>&lt;state dir&gt;/dream-runs.jsonl</c>.</summary>
public static class DreamRunRecordStore
{
    private const string FileName = "dream-runs.jsonl";

    /// <summary>Serialises a record as one JSON line (no newline).</summary>
    /// <param name="record">The record.</param>
    /// <returns>The JSON text.</returns>
    public static string Serialize(DreamRunRecord record) => JsonSerializer.Serialize(record, DreamRecordJsonContext.Default.DreamRunRecord);

    /// <summary>Appends a record to the state directory's log.</summary>
    /// <param name="stateDirectory">The state directory.</param>
    /// <param name="record">The record.</param>
    public static void Append(string stateDirectory, DreamRunRecord record)
    {
        Directory.CreateDirectory(stateDirectory);
        File.AppendAllText(Path.Join(stateDirectory, FileName), Serialize(record) + "\n", new UTF8Encoding(false));
    }

    /// <summary>Returns the newest record, or <see langword="null"/> when no run has been recorded.</summary>
    /// <param name="stateDirectory">The state directory.</param>
    /// <returns>The last record, or <see langword="null"/>.</returns>
    public static DreamRunRecord? ReadLast(string stateDirectory)
    {
        var path = Path.Join(stateDirectory, FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        var last = File.ReadLines(path).LastOrDefault(l => l.Trim().Length > 0);
        if (last is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(last, DreamRecordJsonContext.Default.DreamRunRecord);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(DreamRunRecord))]
internal sealed partial class DreamRecordJsonContext : JsonSerializerContext;
