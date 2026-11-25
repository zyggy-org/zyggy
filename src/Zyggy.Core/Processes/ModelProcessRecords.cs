using System.Globalization;

using Microsoft.Extensions.Logging;

using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Processes;

/// <summary>One model process a node started (spec 61 §4 "Model processes left by a killed node").</summary>
/// <param name="Pid">The model process id.</param>
/// <param name="StartedUtc">When the model process started.</param>
/// <param name="Name">Its process name.</param>
/// <param name="NodePid">The node that started it.</param>
/// <param name="NodeStartedUtc">When that node started; <see langword="null"/> when the system did not say.</param>
internal sealed record ModelProcessRecord(int Pid, DateTimeOffset StartedUtc, string Name, int NodePid, DateTimeOffset? NodeStartedUtc);

/// <summary>
/// Spec 61 slice 8: the node records, per run, the model process it started (pid, start time, name, and the node itself) in
/// <c>&lt;nodeDir&gt;/work/&lt;tenant&gt;/model-processes/</c>, and removes the record when the run ends. At start-up, before claims are
/// resumed, <see cref="EndLeftovers"/> ends every recorded process that provably is that run's process — the recorded pid AND start time
/// (1 s tolerance), this user, the name of the configured model executable (never the name in the file) — whose node is this checkout's
/// previous listener as it recorded itself in its owner record; anything else under that pid (another program, another <c>claude</c> of
/// the user's own sessions) and any record naming another node is never acted on. Only <c>zyggy-node</c> records: a model run of the CLI is never ended here.
/// A record that cannot be written is one Warning and never fails the run.
/// </summary>
internal sealed partial class ModelProcessRecords(string directory, string modelExecutable, IProcessTable processes, ILogger<ModelProcessRecords> log)
{
    private static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan EndWait = TimeSpan.FromSeconds(10);

    /// <summary>The records folder of <paramref name="tenant"/> under <paramref name="nodeDir"/>.</summary>
    public static string DirectoryFor(string nodeDir, TenantId tenant) => Path.Join(nodeDir, "work", tenant.Value, "model-processes");

    /// <summary>Records <paramref name="model"/>, just started by this node; returns the record's path, or <see langword="null"/> when it was not written.</summary>
    public string? Add(ProcessFacts model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.StartedUtc is not { } started)
        {
            LogNotRecorded(model.Pid, "the system did not say when it started");
            return null;
        }

        ProcessFacts node = processes.Self();
        string path = Path.Join(directory, model.Pid.ToString(CultureInfo.InvariantCulture) + "-" + started.UtcTicks.ToString(CultureInfo.InvariantCulture) + ".txt");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, string.Join('\n', [
                "pid=" + model.Pid.ToString(CultureInfo.InvariantCulture),
                "started=" + Stamp(started),
                "name=" + model.Name,
                "node_pid=" + node.Pid.ToString(CultureInfo.InvariantCulture),
                "node_started=" + (node.StartedUtc is { } n ? Stamp(n) : string.Empty),
            ]) + "\n");
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogNotRecorded(model.Pid, ex.Message);
            return null;
        }
    }

    /// <summary>Removes the record of a run that ended.</summary>
    public void Remove(string? record)
    {
        if (record is null)
        {
            return;
        }

        try
        {
            File.Delete(record);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogNotRemoved(record, ex.Message);
        }
    }

    /// <summary>The records in the folder (unreadable ones left out).</summary>
    public IReadOnlyList<ModelProcessRecord> Read() =>
        [.. Files().Select(f => Parse(f)).OfType<ModelProcessRecord>()];

    /// <summary>
    /// Ends every recorded model process of a node that is gone, when it is provably still that process; clears every record whose node
    /// is gone (also an unreadable one). One Warning per process ended (or not endable).
    /// </summary>
    /// <returns>The number of processes ended.</returns>
    public int EndLeftovers(Bus.ListenerRecord? previousListener)
    {
        int ended = 0;
        string modelName = Path.GetFileNameWithoutExtension(modelExecutable);
        ProcessFacts self = processes.Self();
        foreach (string file in Files())
        {
            if (Parse(file) is not { } record)
            {
                LogUnreadable(Path.GetFileName(file));
                Remove(file);
                continue;
            }

            // A record of a node that still runs is that node's live run, never a leftover.
            if (record.NodePid != self.Pid && record.NodeStartedUtc is { } nodeStarted && processes.Find(record.NodePid) is { } node && Same(node.StartedUtc, nodeStarted))
            {
                continue;
            }

            // The record file is not authenticated: its node must be this checkout's previous listener as that listener recorded itself
            // (the owner record found when the lock was taken), and the name to match is the configured model executable's, never the
            // record's. A planted record naming another program or another node never leads to a kill.
            if (previousListener is null
                || record.NodePid != previousListener.Pid
                || record.NodeStartedUtc is not { } recordedNodeStart
                || !Same(previousListener.ProcessStartUtc, recordedNodeStart))
            {
                LogForeignRecord(Path.GetFileName(file));
                Remove(file);
                continue;
            }

            if (processes.Find(record.Pid) is { } facts
                && facts.OwnedByCurrentUser
                && IsModelName(facts.Name, modelName)
                && Same(facts.StartedUtc, record.StartedUtc))
            {
                if (processes.EndTree(record.Pid, EndWait) is { } why)
                {
                    LogNotEnded(record.Pid, facts.Name, Stamp(record.StartedUtc), why);
                }
                else
                {
                    LogEnded(record.Pid, facts.Name, Stamp(record.StartedUtc));
                    ended++;
                }
            }

            Remove(file);
        }

        return ended;
    }

    // Linux reports at most 15 characters of a process name (comm).
    private const int LinuxNameLength = 15;

    private static bool IsModelName(string actual, string configured) =>
        configured.Length > 0
        && (string.Equals(actual, configured, StringComparison.OrdinalIgnoreCase)
            || (configured.Length > LinuxNameLength && actual.Length == LinuxNameLength && configured.StartsWith(actual, StringComparison.OrdinalIgnoreCase)));

    private static bool Same(DateTimeOffset? actual, DateTimeOffset recorded) => actual is { } a && (a - recorded).Duration() <= Tolerance;

    private static string Stamp(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private List<string> Files()
    {
        try
        {
            return Directory.Exists(directory) ? Directory.GetFiles(directory, "*.txt").Order(StringComparer.Ordinal).ToList() : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogNotRemoved(directory, ex.Message);
            return [];
        }
    }

    private static ModelProcessRecord? Parse(string file)
    {
        Dictionary<string, string> fields;
        try
        {
            fields = File.ReadAllLines(file)
                .Select(l => l.Split('=', 2))
                .Where(p => p.Length == 2)
                .GroupBy(p => p[0], StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Last()[1], StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return fields.TryGetValue("pid", out string? pid) && int.TryParse(pid, NumberStyles.None, CultureInfo.InvariantCulture, out int modelPid) && modelPid > 0
            && fields.TryGetValue("started", out string? started) && DateTimeOffset.TryParse(started, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset modelStart)
            && fields.TryGetValue("name", out string? name) && name.Length > 0
            && fields.TryGetValue("node_pid", out string? nodePid) && int.TryParse(nodePid, NumberStyles.None, CultureInfo.InvariantCulture, out int node)
                ? new ModelProcessRecord(
                    modelPid,
                    modelStart,
                    name,
                    node,
                    fields.TryGetValue("node_started", out string? ns) && DateTimeOffset.TryParse(ns, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset nodeStart) ? nodeStart : null)
                : null;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "model process {Pid} ({Name}, started {Started}) of a run of a node that was killed was still running; ended")]
    private partial void LogEnded(int pid, string name, string started);

    [LoggerMessage(Level = LogLevel.Warning, Message = "model process {Pid} ({Name}, started {Started}) of a run of a node that was killed is still running and could not be ended ({Why})")]
    private partial void LogNotEnded(int pid, string name, string started, string why);

    [LoggerMessage(Level = LogLevel.Warning, Message = "model process {Pid} not recorded in the node folder ({Why}); a node killed during this run leaves it running")]
    private partial void LogNotRecorded(int pid, string why);

    [LoggerMessage(Level = LogLevel.Warning, Message = "model process record {Path} not removed ({Why})")]
    private partial void LogNotRemoved(string path, string why);

    [LoggerMessage(Level = LogLevel.Warning, Message = "model process record {File} names no run of this node's previous listener; removed, nothing ended")]
    private partial void LogForeignRecord(string file);

    [LoggerMessage(Level = LogLevel.Warning, Message = "model process record {File} is unreadable; removed, nothing ended")]
    private partial void LogUnreadable(string file);
}
