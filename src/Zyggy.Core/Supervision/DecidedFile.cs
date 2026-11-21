using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

using Zyggy.Core.Brief;
using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Supervision;

/// <summary>
/// The hand-over from Central's node to the morning brief (deliverable 18, "Record"): the decisions of the last 14 days as time, kind,
/// machine, project and id — never a decision's text, never a work machine's project — in <c>decided.json</c> beside the brief files.
/// The node writes it whole through a temporary file when its content changed; the brief reads the lines created after the previous
/// brief. A missing or unreadable file means no section, never a failure.
/// </summary>
internal static class DecidedFile
{
    /// <summary>How long a decision stays in the file.</summary>
    internal static readonly TimeSpan Keep = TimeSpan.FromDays(14);

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>Writes the file when its content changed.</summary>
    /// <param name="path">The file.</param>
    /// <param name="decisions">Central's decisions, in any order.</param>
    /// <param name="isWorkMachine">Whether a machine is a work machine: its decisions carry no project.</param>
    /// <param name="now">The time the 14 days are counted back from.</param>
    /// <returns><see langword="true"/> when the file was written.</returns>
    /// <exception cref="IOException">Thrown when the file cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the folder may not be written.</exception>
    public static bool Write(string path, IReadOnlyList<DecisionRecord> decisions, Func<MachineName, bool> isWorkMachine, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(isWorkMachine);
        Entry[] entries = [.. decisions
            .Where(d => d.Created > now - Keep)
            .OrderBy(d => d.Created)
            .ThenBy(d => d.Id.Value, StringComparer.Ordinal)
            .Select(d => new Entry(d.Created.ToUniversalTime(), EnvelopeWire.ToWire(d.Fields.Kind), d.To.Value, isWorkMachine(d.To) ? null : d.Fields.Project, d.Id.Value))];
        string text = JsonSerializer.Serialize(entries, Json) + "\n";
        if (File.Exists(path) && string.Equals(File.ReadAllText(path), text, StringComparison.Ordinal))
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, text);
        File.Move(temporary, path, overwrite: true);
        return true;
    }

    /// <summary>The lines of the decisions created after <paramref name="since"/>, oldest first, times in <paramref name="zone"/>.</summary>
    /// <param name="path">The file.</param>
    /// <param name="since">The previous brief's time.</param>
    /// <param name="zone">The owner's time zone.</param>
    /// <returns>The lines; none when the file is missing or unreadable.</returns>
    public static IReadOnlyList<DecidedLine> Read(string path, DateTimeOffset since, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        Entry[]? entries;
        try
        {
            entries = File.Exists(path) ? JsonSerializer.Deserialize<Entry[]>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return [];
        }

        if (entries is null || entries.Any(e => e is null || e.Kind is null || e.Machine is null || e.Id is null))
        {
            return [];
        }

        return [.. entries
            .Where(e => e.Time > since)
            .OrderBy(e => e.Time)
            .Select(e => new DecidedLine(
                TimeZoneInfo.ConvertTime(e.Time, zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                e.Kind,
                e.Machine,
                e.Project,
                e.Id))];
    }

    private sealed record Entry(DateTimeOffset Time, string Kind, string Machine, string? Project, string Id);
}
