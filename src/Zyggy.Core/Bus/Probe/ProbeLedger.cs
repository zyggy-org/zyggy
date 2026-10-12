using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Bus.Probe;

/// <summary>
/// The sender's append-only probe ledger: one JSON object per line at <c>&lt;LocalApplicationData&gt;/zyggy/probe/&lt;tenant&gt;/sent.jsonl</c>
/// (deliverable 04). <c>probe stats</c> reads it; nothing else does. It is the sender's only record (O21) and never touches the bus.
/// </summary>
public sealed class ProbeLedger
{
    private readonly string _path;

    /// <summary>Creates a ledger at <paramref name="path"/>; the file and its folders are created on the first append.</summary>
    /// <param name="path">The ledger file.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="path"/> is null or empty.</exception>
    public ProbeLedger(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _path = path;
    }

    /// <summary>Gets the ledger file path.</summary>
    public string Path => _path;

    /// <summary>The default ledger path for <paramref name="tenant"/> under <paramref name="localApplicationData"/>.</summary>
    /// <param name="localApplicationData">The per-user application data folder.</param>
    /// <param name="tenant">The tenant.</param>
    /// <returns>The path.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="tenant"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="localApplicationData"/> is null or empty.</exception>
    public static string DefaultPath(string localApplicationData, TenantId tenant)
    {
        ArgumentException.ThrowIfNullOrEmpty(localApplicationData);
        ArgumentNullException.ThrowIfNull(tenant);
        return System.IO.Path.Join(localApplicationData, "zyggy", "probe", tenant.Value, "sent.jsonl");
    }

    /// <summary>Appends one record as one line.</summary>
    /// <param name="record">The record.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the line is written.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="record"/> is null.</exception>
    public async Task AppendAsync(ProbeRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        string line = JsonSerializer.Serialize(ProbeLine.From(record), ProbeLedgerJsonContext.Default.ProbeLine) + "\n";
        await File.AppendAllTextAsync(_path, line, new UTF8Encoding(false), ct).ConfigureAwait(false);
    }

    /// <summary>Every record in file order; an absent file is an empty ledger. A line that does not parse is skipped.</summary>
    /// <returns>The records.</returns>
    public IReadOnlyList<ProbeRecord> ReadAll()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        var records = new List<ProbeRecord>();
        foreach (string line in File.ReadLines(_path))
        {
            if (line.Length == 0)
            {
                continue;
            }

            ProbeLine? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize(line, ProbeLedgerJsonContext.Default.ProbeLine);
            }
            catch (JsonException)
            {
                continue;
            }

            if (parsed?.ToRecord() is { } record)
            {
                records.Add(record);
            }
        }

        return records;
    }

    /// <summary>The JSON shape of one line: wire strings for the id, machine, outcome and timestamps.</summary>
    internal sealed record ProbeLine(
        string Id,
        string To,
        string SentUtc,
        string? ObservedUtc,
        string Outcome,
        string? Reason,
        long? RoundTripMs)
    {
        private const string Format = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

        public static ProbeLine From(ProbeRecord record) => new(
            record.Id.Value,
            record.To.Value,
            record.SentUtc.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture),
            record.ObservedUtc?.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture),
            ProbeOutcomeWire.ToWire(record.Outcome),
            record.Reason,
            record.RoundTripMs);

        public ProbeRecord? ToRecord()
        {
            if (!EnvelopeId.TryParse(Id, out EnvelopeId? id)
                || !MachineName.TryParse(To, out MachineName? to)
                || !ProbeOutcomeWire.TryFromWire(Outcome, out ProbeOutcome outcome)
                || !DateTimeOffset.TryParse(SentUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset sent))
            {
                return null;
            }

            DateTimeOffset? observed = ObservedUtc is not null
                && DateTimeOffset.TryParse(ObservedUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset o)
                ? o
                : null;
            return new ProbeRecord(id, to, sent, observed, outcome, Reason, RoundTripMs);
        }
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(ProbeLedger.ProbeLine))]
internal sealed partial class ProbeLedgerJsonContext : JsonSerializerContext;
