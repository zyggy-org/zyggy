using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Zyggy.Core.Memory;

/// <summary>One item's offload failures: the consecutive count, the last reason, whether it is parked, and since when it fails.</summary>
internal sealed record OffloadStateItem(int Failures, OffloadReason Reason, bool Parked, DateOnly Since);

/// <summary>The last run: when it ended, whether it failed, and the run-level reason.</summary>
internal sealed record OffloadLastRun(DateTimeOffset At, bool Failed, OffloadReason? Reason);

/// <summary>A loaded state, or the error that kept it from loading.</summary>
internal sealed record OffloadStateLoad(OffloadState? State, string? Error);

/// <summary>
/// The archive offload's failure state (spec 38 AC-19..AC-21, plan assumption A8):
/// <c>{"items":{"&lt;project&gt;/&lt;slug&gt;":{"failures","reason","parked","since"}},"last_run":{"at","failed","reason"}}</c> in
/// <c>&lt;state&gt;/archive/offload-state.json</c>, 0600 in a 0700 directory, written atomically. Machine-local; never in git.
/// </summary>
internal sealed class OffloadState
{
    private static readonly JsonWriterOptions JsonOptions = new() { Indented = true, NewLine = "\n", Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly SortedDictionary<string, OffloadStateItem> _items = new(StringComparer.Ordinal);

    /// <summary>Gets every item with failures, by <c>&lt;project&gt;/&lt;slug&gt;</c>.</summary>
    public IReadOnlyDictionary<string, OffloadStateItem> Items => _items;

    /// <summary>Gets the last run, once one was recorded.</summary>
    public OffloadLastRun? LastRun { get; private set; }

    /// <summary>Gets the parked items' keys, in ordinal order.</summary>
    public IReadOnlyList<string> Parked => [.. _items.Where(i => i.Value.Parked).Select(i => i.Key)];

    /// <summary>Gets whether anything needs the owner: a parked item, or a failed last run.</summary>
    public bool NeedsAttention => _items.Values.Any(i => i.Parked) || LastRun is { Failed: true };

    /// <summary>The key of an item: <c>&lt;project&gt;/&lt;slug&gt;</c>.</summary>
    public static string Key(Slug project, Slug slug)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(slug);
        return project.Value + "/" + slug.Value;
    }

    /// <summary>Reads the state; a missing file is the empty state, an unreadable or invalid one an error naming the file.</summary>
    public static OffloadStateLoad Load(ArchiveStatePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (!File.Exists(paths.StateFile))
        {
            return new OffloadStateLoad(new OffloadState(), null);
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(paths.StateFile));
            return Parse(document.RootElement) is { } state
                ? new OffloadStateLoad(state, null)
                : new OffloadStateLoad(null, "offload-state.json is not a valid state file");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new OffloadStateLoad(null, "offload-state.json is not readable");
        }
    }

    /// <summary>Counts one more transient failure; parks the item once the count reaches <paramref name="maxFailures"/>.</summary>
    public OffloadStateItem RecordTransient(string key, OffloadReason reason, int maxFailures, DateOnly today)
    {
        var failures = (_items.TryGetValue(key, out var item) ? item.Failures : 0) + 1;
        return _items[key] = new OffloadStateItem(failures, reason, failures >= maxFailures, item?.Since ?? today);
    }

    /// <summary>Parks the item at once (a permanent reason).</summary>
    public OffloadStateItem Park(string key, OffloadReason reason, DateOnly today)
    {
        var failures = (_items.TryGetValue(key, out var item) ? item.Failures : 0) + 1;
        return _items[key] = new OffloadStateItem(failures, reason, true, item?.Since ?? today);
    }

    /// <summary>Forgets the item's failures (it moved).</summary>
    public void Clear(string key) => _items.Remove(key);

    /// <summary>Clears a parked item so the run takes it again; <see langword="false"/> when the item is unknown or not parked.</summary>
    public bool TryRetry(string key) => _items.TryGetValue(key, out var item) && item.Parked && _items.Remove(key);

    /// <summary>Records how the run ended.</summary>
    public void RecordRun(DateTimeOffset at, bool failed, OffloadReason? reason) => LastRun = new OffloadLastRun(at, failed, reason);

    /// <summary>Writes the state atomically: 0600 in a 0700 directory on Linux.</summary>
    /// <exception cref="IOException">Thrown when the file cannot be written.</exception>
    public void Save(ArchiveStatePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        const UnixFileMode Directory0700 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        const UnixFileMode File0600 = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(paths.StateDirectory);
        }
        else
        {
            Directory.CreateDirectory(paths.StateDirectory, Directory0700);
            File.SetUnixFileMode(paths.StateDirectory, Directory0700);
        }

        var temp = paths.StateFile + ".zyggy-tmp-" + Ulid.NewUlid();
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = File0600;
            }

            using (var stream = new FileStream(temp, options))
            {
                stream.Write(Encoding.UTF8.GetBytes(ToJson()));
            }

            File.Move(temp, paths.StateFile, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    /// <summary>The state's JSON text (A8 shape), items in ordinal key order.</summary>
    public string ToJson()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, JsonOptions))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("items");
            foreach (var (key, item) in _items)
            {
                writer.WriteStartObject(key);
                writer.WriteNumber("failures", item.Failures);
                writer.WriteString("reason", OffloadReasonWire.ToWire(item.Reason));
                writer.WriteBoolean("parked", item.Parked);
                writer.WriteString("since", item.Since.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            if (LastRun is { } run)
            {
                writer.WriteStartObject("last_run");
                writer.WriteString("at", run.At.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
                writer.WriteBoolean("failed", run.Failed);
                if (run.Reason is { } reason)
                {
                    writer.WriteString("reason", OffloadReasonWire.ToWire(reason));
                }
                else
                {
                    writer.WriteNull("reason");
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    private static OffloadState? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var state = new OffloadState();
        foreach (var property in items.EnumerateObject())
        {
            var item = property.Value;
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("failures", out var failures) || failures.ValueKind != JsonValueKind.Number || !failures.TryGetInt32(out var count) || count < 1
                || !item.TryGetProperty("reason", out var reason) || !OffloadReasonWire.TryFromWire(reason.ValueKind == JsonValueKind.String ? reason.GetString() : null, out var why)
                || !item.TryGetProperty("parked", out var parked) || parked.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !item.TryGetProperty("since", out var since) || since.ValueKind != JsonValueKind.String
                || !DateOnly.TryParseExact(since.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from))
            {
                return null;
            }

            state._items[property.Name] = new OffloadStateItem(count, why, parked.ValueKind == JsonValueKind.True, from);
        }

        if (root.TryGetProperty("last_run", out var run) && run.ValueKind == JsonValueKind.Object)
        {
            if (!run.TryGetProperty("at", out var at) || at.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParseExact(at.GetString(), "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when)
                || !run.TryGetProperty("failed", out var failed) || failed.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return null;
            }

            OffloadReason? reason = run.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String && OffloadReasonWire.TryFromWire(r.GetString(), out var w) ? w : null;
            state.LastRun = new OffloadLastRun(when, failed.ValueKind == JsonValueKind.True, reason);
        }

        return state;
    }
}
