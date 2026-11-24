using System.Globalization;
using System.Text.Json;

using Zyggy.Core.Processes;

namespace Zyggy.Core.Bus;

/// <summary>Who holds a checkout's listen lock (spec 61 §4): pid, process start time, version, started (UTC). Nothing else.</summary>
/// <param name="Pid">The listener's process id.</param>
/// <param name="ProcessStartUtc">When that process started, as the system reports it (tells a reused pid apart).</param>
/// <param name="Version">The listener's version.</param>
/// <param name="StartedUtc">When the listener took the lock.</param>
internal sealed record ListenerRecord(int Pid, DateTimeOffset ProcessStartUtc, string Version, DateTimeOffset StartedUtc);

/// <summary>A start that found the lock held (spec 61 §4): when, and which holder it met.</summary>
/// <param name="RefusedUtc">When the start was refused.</param>
/// <param name="HolderPid">The holder's pid from its record; <see langword="null"/> when the holder left no record.</param>
internal sealed record RefusedStart(DateTimeOffset RefusedUtc, int? HolderPid);

/// <summary>
/// The owner record beside the listen lock and the refused-start record (spec 61 §4), both in the checkout's <c>.git</c> directory, never
/// committed: <c>zyggy-listen.owner</c> and <c>zyggy-listen.refused</c>. A checkout without <c>.git</c> gets no record (as the lock).
/// </summary>
internal sealed class ListenerOwner
{
    internal const string OwnerFileName = "zyggy-listen.owner";
    internal const string RefusedFileName = "zyggy-listen.refused";

    /// <summary>Two start times this close apart are the same process start (the system's clock resolution differs per API).</summary>
    internal static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(1);

    private readonly string _checkoutRoot;
    private readonly IProcessTable _processes;
    private readonly string _version;
    private readonly TimeProvider _clock;

    public ListenerOwner(string checkoutRoot, IProcessTable processes, string version, TimeProvider clock)
    {
        _checkoutRoot = checkoutRoot;
        _processes = processes;
        _version = version;
        _clock = clock;
    }

    /// <summary>The record this process wrote when it took the lock; <see langword="null"/> before.</summary>
    public ListenerRecord? Current { get; private set; }

    /// <summary>Writes this process as the holder (called right after the lock was taken).</summary>
    public ListenerRecord Record()
    {
        ProcessFacts self = _processes.Self();
        DateTimeOffset now = _clock.GetUtcNow();
        var record = new ListenerRecord(self.Pid, self.StartedUtc ?? now, _version, now);
        Current = record;
        if (GitDirectory(_checkoutRoot) is { } git)
        {
            TryWrite(Path.Join(git, OwnerFileName), Serialize(record));
        }

        return record;
    }

    /// <summary>Removes the owner record if it is still this process's (a clean stop).</summary>
    public void Release()
    {
        if (Current is null || GitDirectory(_checkoutRoot) is not { } git)
        {
            return;
        }

        if (ReadOwner(_checkoutRoot) is { } onDisk && onDisk.Pid == Current.Pid && onDisk.StartedUtc == Current.StartedUtc)
        {
            TryDelete(Path.Join(git, OwnerFileName));
        }

        Current = null;
    }

    /// <summary>Notes a start refused because the lock is held, and returns the holder's record (or <see langword="null"/> when it left none).</summary>
    public ListenerRecord? NoteRefused()
    {
        ListenerRecord? holder = ReadOwner(_checkoutRoot);
        if (GitDirectory(_checkoutRoot) is { } git)
        {
            TryWrite(Path.Join(git, RefusedFileName), Serialize(new RefusedStart(_clock.GetUtcNow(), holder?.Pid)));
        }

        return holder;
    }

    /// <summary>The refusal sentence of spec 61 §4 for <paramref name="holder"/>.</summary>
    public static string RefusalDetail(ListenerRecord? holder) => holder is null
        ? "listener already running (it left no owner record: a zyggy-node before 0.30.1 or a zyggy probe listen) — stop it with: zyggy node stop"
        : "listener already running (pid " + holder.Pid.ToString(CultureInfo.InvariantCulture) + " since " + Timestamp(holder.StartedUtc)
            + ", zyggy-node " + holder.Version + ") — stop it with: zyggy node stop";

    /// <summary>The owner record of <paramref name="checkoutRoot"/>; <see langword="null"/> when absent or unreadable.</summary>
    public static ListenerRecord? ReadOwner(string checkoutRoot)
    {
        if (ReadJson(checkoutRoot, OwnerFileName) is not { } root)
        {
            return null;
        }

        try
        {
            using (root)
            {
                JsonElement e = root.RootElement;
                return new ListenerRecord(
                    e.GetProperty("pid").GetInt32(),
                    Instant(e.GetProperty("processStartUtc")),
                    e.GetProperty("version").GetString() ?? "unknown",
                    Instant(e.GetProperty("startedUtc")));
            }
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>The refused-start record of <paramref name="checkoutRoot"/>; <see langword="null"/> when absent or unreadable.</summary>
    public static RefusedStart? ReadRefused(string checkoutRoot)
    {
        if (ReadJson(checkoutRoot, RefusedFileName) is not { } root)
        {
            return null;
        }

        try
        {
            using (root)
            {
                JsonElement e = root.RootElement;
                int? holder = e.TryGetProperty("holderPid", out JsonElement pid) && pid.ValueKind == JsonValueKind.Number ? pid.GetInt32() : null;
                return new RefusedStart(Instant(e.GetProperty("refusedUtc")), holder);
            }
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Removes both records (after <c>zyggy node stop</c> ended their listener).</summary>
    public static void Clear(string checkoutRoot)
    {
        if (GitDirectory(checkoutRoot) is { } git)
        {
            TryDelete(Path.Join(git, OwnerFileName));
            TryDelete(Path.Join(git, RefusedFileName));
        }
    }

    /// <summary>Writes the owner record (tests, and the stand-in of a listener).</summary>
    internal static void WriteOwner(string checkoutRoot, ListenerRecord record) =>
        TryWrite(Path.Join(checkoutRoot, ".git", OwnerFileName), Serialize(record));

    /// <summary>Writes the refused-start record (tests).</summary>
    internal static void WriteRefused(string checkoutRoot, RefusedStart refused) =>
        TryWrite(Path.Join(checkoutRoot, ".git", RefusedFileName), Serialize(refused));

    /// <summary>Whether <paramref name="facts"/> is the process <paramref name="record"/> names: the same id and the same start.</summary>
    public static bool IsSameProcess(ListenerRecord record, ProcessFacts? facts) =>
        facts is { StartedUtc: { } started } && facts.Pid == record.Pid && (started - record.ProcessStartUtc).Duration() <= StartTolerance;

    internal static string Timestamp(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string? GitDirectory(string checkoutRoot)
    {
        string git = Path.Join(checkoutRoot, ".git");
        return Directory.Exists(git) ? git : null;
    }

    private static JsonDocument? ReadJson(string checkoutRoot, string name)
    {
        try
        {
            string path = Path.Join(checkoutRoot, ".git", name);
            return File.Exists(path) ? JsonDocument.Parse(File.ReadAllBytes(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static DateTimeOffset Instant(JsonElement value) =>
        DateTimeOffset.Parse(value.GetString() ?? string.Empty, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static byte[] Serialize(ListenerRecord record) => Json(w =>
    {
        w.WriteNumber("pid", record.Pid);
        w.WriteString("processStartUtc", record.ProcessStartUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        w.WriteString("version", record.Version);
        w.WriteString("startedUtc", record.StartedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
    });

    private static byte[] Serialize(RefusedStart refused) => Json(w =>
    {
        w.WriteString("refusedUtc", refused.RefusedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        if (refused.HolderPid is { } pid)
        {
            w.WriteNumber("holderPid", pid);
        }
        else
        {
            w.WriteNull("holderPid");
        }
    });

    private static byte[] Json(Action<Utf8JsonWriter> body)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }

        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    private static void TryWrite(string path, byte[] bytes)
    {
        try
        {
            string temp = path + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the record is information for status and node stop; the listener never fails for it
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // left for the next start to replace
        }
    }
}
