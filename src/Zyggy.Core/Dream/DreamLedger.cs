using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Zyggy.Core.Dream;

/// <summary>How <see cref="DreamLedger.Load"/> ended.</summary>
internal enum DreamLedgerLoadStatus
{
    Loaded,
    Unsupported,
    Invalid,
}

/// <summary>The outcome of loading a ledger.</summary>
internal sealed record DreamLedgerLoad(DreamLedgerLoadStatus Status, DreamLedger? Ledger);

/// <summary>
/// <c>.dream/ledger.json</c>: per inbox or daily file, the hashes of the body lines a committed batch consumed and the local date
/// of the last consumption (spec 28). Committed in the same commit as the edits it caused.
/// </summary>
internal sealed class DreamLedger
{
    private const int Schema = 1;

    private readonly SortedDictionary<string, Entry> _files = new(StringComparer.Ordinal);

    private DreamLedger()
    {
    }

    public IReadOnlyCollection<string> Files => _files.Keys;

    public static DreamLedger Empty() => new();

    public static DreamLedgerLoad Load(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new DreamLedgerLoad(DreamLedgerLoadStatus.Loaded, Empty());
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Number)
            {
                return new DreamLedgerLoad(DreamLedgerLoadStatus.Invalid, null);
            }

            if (schema.GetInt32() != Schema)
            {
                return new DreamLedgerLoad(DreamLedgerLoadStatus.Unsupported, null);
            }

            var ledger = Empty();
            if (!root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Object)
            {
                return new DreamLedgerLoad(DreamLedgerLoadStatus.Invalid, null);
            }

            foreach (var file in files.EnumerateObject())
            {
                var entry = new Entry();
                foreach (var hash in file.Value.GetProperty("consumed").EnumerateArray())
                {
                    entry.Consumed.Add(hash.GetString() ?? throw new FormatException("A consumed hash is null."));
                }

                entry.LastConsumed = DateOnly.ParseExact(file.Value.GetProperty("lastConsumed").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                ledger._files[file.Name] = entry;
            }

            return new DreamLedgerLoad(DreamLedgerLoadStatus.Loaded, ledger);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or KeyNotFoundException)
        {
            return new DreamLedgerLoad(DreamLedgerLoadStatus.Invalid, null);
        }
    }

    public bool IsConsumed(string relativePath, string hash) => _files.TryGetValue(relativePath, out var entry) && entry.Consumed.Contains(hash);

    public bool AllConsumed(string relativePath, IEnumerable<string> hashes) =>
        _files.TryGetValue(relativePath, out var entry) && hashes.All(entry.Consumed.Contains);

    public DateOnly? LastConsumed(string relativePath) => _files.TryGetValue(relativePath, out var entry) ? entry.LastConsumed : null;

    public void Consume(string relativePath, IEnumerable<string> hashes, DateOnly date)
    {
        if (!_files.TryGetValue(relativePath, out var entry))
        {
            entry = new Entry();
            _files[relativePath] = entry;
        }

        entry.Consumed.UnionWith(hashes);
        entry.LastConsumed = entry.LastConsumed is { } last && last > date ? last : date;
    }

    public void Remove(string relativePath) => _files.Remove(relativePath);

    public DreamLedger Clone() => Load(Serialize()).Ledger!;

    /// <summary>The ledger as JSON: keys in ordinal order, hashes sorted, two-space indentation, LF, final newline.</summary>
    public string Serialize()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema", Schema);
            writer.WriteStartObject("files");
            foreach (var (path, entry) in _files)
            {
                writer.WriteStartObject(path);
                writer.WriteStartArray("consumed");
                foreach (var hash in entry.Consumed)
                {
                    writer.WriteStringValue(hash);
                }

                writer.WriteEndArray();
                writer.WriteString("lastConsumed", (entry.LastConsumed ?? DateOnly.MinValue).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    private sealed class Entry
    {
        public SortedSet<string> Consumed { get; } = new(StringComparer.Ordinal);

        public DateOnly? LastConsumed { get; set; }
    }
}
