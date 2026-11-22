using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Zyggy.Core.Memory;

namespace Zyggy.Core.Outlook;

/// <summary>
/// The calls the outlook guard passed to the owner's permission prompt and that have not run yet (spec 58 slice 7, AC-12):
/// <c>pending.jsonl</c> beside the action log on the laptop, one line per pass, keyed by the SHA-256 of the exact request and holding
/// the SHA-256 of the pass's random nonce (never the nonce: the guard hands it only to Claude Code, inside the command it runs on
/// Allow). The action tool runs only a call whose pass it can take (same request, same nonce, younger than <see cref="Lifetime"/>),
/// once; every read-change-write holds <c>pending.lock</c>, so two processes cannot both take one pass. A pass that is never taken was
/// denied at the prompt (or never answered): <c>zyggy outlook settle</c> (the session's Stop hook) or the session's next guarded call
/// turns it into one <c>denied</c> row, as does any action once it expired. Claude Code tells no hook about a denied prompt; this is
/// how the row is written anyway. The file holds the log row's fields only, never a body.
/// </summary>
/// <param name="stateDirectory">The laptop's outlook state directory (the action log's).</param>
internal sealed class OutlookActionPending(string stateDirectory)
{
    /// <summary>How long a pass stays valid: an Allow later than this does not send; the pass is settled as denied.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    /// <summary>Gets the file's path.</summary>
    public string FilePath => Path.Join(stateDirectory, "pending.jsonl");

    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(10);

    /// <summary>The hash of a pass's nonce, the only form of it the file keeps: SHA-256, lowercase hex.</summary>
    /// <param name="nonce">The nonce.</param>
    /// <returns>The hash.</returns>
    public static string NonceHash(string nonce)
    {
        ArgumentNullException.ThrowIfNull(nonce);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(nonce)));
    }

    /// <summary>Gets the lock file's path: every read-change-write of the passes holds it.</summary>
    public string LockPath => Path.Join(stateDirectory, "pending.lock");

    /// <summary>Records one pass.</summary>
    /// <param name="key">The request's key.</param>
    /// <param name="nonce">The pass's nonce, which the guard put into the command; only its hash is kept.</param>
    /// <param name="row">The row the call would get if denied (time and outcome are set when it is settled).</param>
    /// <param name="now">The time of the pass.</param>
    public void Add(string key, string nonce, OutlookActionLogRow row, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentException.ThrowIfNullOrEmpty(nonce);
        ArgumentNullException.ThrowIfNull(row);
        Locked(() =>
        {
            var entries = Load();
            entries.Add(new Entry(key, NonceHash(nonce), now, row));
            Save(entries);
            return true;
        });
    }

    /// <summary>
    /// Takes (removes) the valid pass of <paramref name="key"/> whose nonce is <paramref name="nonce"/>, atomically: load, check,
    /// remove and save happen under the lock, so of two processes running the same call at most one takes the pass.
    /// </summary>
    /// <param name="key">The request's key.</param>
    /// <param name="nonce">The nonce the command carries; without it no pass is taken (and none is touched).</param>
    /// <param name="now">The time of the action.</param>
    /// <returns>Whether a valid pass was found and taken.</returns>
    public bool Take(string key, string? nonce, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (string.IsNullOrEmpty(nonce))
        {
            return false;
        }

        string hash = NonceHash(nonce);
        return Locked(() =>
        {
            var entries = Load();
            int index = entries.FindIndex(e => e.Key == key && e.NonceHash == hash && now - e.Time <= Lifetime && e.Time <= now);
            if (index < 0)
            {
                return false;
            }

            entries.RemoveAt(index);
            Save(entries);
            return true;
        });
    }

    /// <summary>Removes the expired passes (or every pass) and returns their <c>denied</c> rows.</summary>
    /// <param name="now">The time of the settling.</param>
    /// <param name="all">Whether every pass is settled (the session ended, or went on to another call), not only the expired ones.</param>
    /// <returns>The rows to append, oldest pass first.</returns>
    public IReadOnlyList<OutlookActionLogRow> Settle(DateTimeOffset now, bool all) => Locked<IReadOnlyList<OutlookActionLogRow>>(() =>
    {
        var entries = Load();
        var settled = entries.Where(e => all || now - e.Time > Lifetime).ToList();
        if (settled.Count == 0)
        {
            return [];
        }

        Save([.. entries.Except(settled)]);
        return [.. settled.Select(e => e.Row with { Time = now, Outcome = OutlookActionOutcome.Denied, Reason = "not allowed at the prompt" })];
    });

    // One process at a time: the lock file opened exclusively (FileShare.None; flock on Unix) for the whole read-change-write.
    // Waits up to LockWait, then throws (the caller fails closed: no pass taken or given).
    private T Locked<T>(Func<T> body)
    {
        Directory.CreateDirectory(stateDirectory);
        var waited = Stopwatch.StartNew();
        while (true)
        {
            FileStream held;
            try
            {
                held = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && waited.Elapsed < LockWait)
            {
                Thread.Sleep(10);
                continue;
            }

            using (held)
            {
                return body();
            }
        }
    }

    private List<Entry> Load()
    {
        if (!File.Exists(FilePath))
        {
            return [];
        }

        var entries = new List<Entry>();
        foreach (string line in File.ReadAllLines(FilePath))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                entries.Add(Entry.Read(document.RootElement));
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                // A line this program did not write is no pass.
            }
        }

        return entries;
    }

    private void Save(List<Entry> entries)
    {
        Directory.CreateDirectory(stateDirectory);
        MemoryFileWriter.WriteAtomically(FilePath, string.Concat(entries.Select(e => e.Write() + "\n")));
    }

    private sealed record Entry(string Key, string NonceHash, DateTimeOffset Time, OutlookActionLogRow Row)
    {
        public static Entry Read(JsonElement e)
        {
            static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var time = DateTimeOffset.ParseExact(Text(e, "ts")!, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
            var action = Text(e, "action") == "move" ? OutlookAction.Move : OutlookAction.Send;
            List<string> to = e.TryGetProperty("to", out var t) && t.ValueKind == JsonValueKind.Array ? [.. t.EnumerateArray().Select(x => x.GetString()!)] : [];
            return new Entry(
                Text(e, "key")!,
                Text(e, "nonce") ?? string.Empty,
                time,
                new OutlookActionLogRow(time, action, OutlookActionOutcome.Denied, null, Text(e, "reference"), to, Text(e, "subject"), Text(e, "destination")));
        }

        public string Write()
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("key", Key);
                writer.WriteString("nonce", NonceHash);
                writer.WriteString("ts", Time.UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture));
                writer.WriteString("action", Row.Action == OutlookAction.Move ? "move" : "send");
                Optional(writer, "reference", Row.Reference);
                writer.WriteStartArray("to");
                foreach (string recipient in Row.Recipients)
                {
                    writer.WriteStringValue(recipient);
                }

                writer.WriteEndArray();
                Optional(writer, "subject", Row.Subject);
                Optional(writer, "destination", Row.Destination);
                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        private static void Optional(Utf8JsonWriter writer, string name, string? value)
        {
            if (value is not null)
            {
                writer.WriteString(name, value);
            }
        }
    }
}
