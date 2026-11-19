using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;

using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Configuration;

/// <summary>The five <c>node.json</c> keys a running node applies (spec 55), replaced together as one unit.</summary>
/// <param name="DevRoots">The <c>devRoots</c>: the scan's roots and the folders a job path may name.</param>
/// <param name="Exclude">The owner's <c>discovery.exclude</c> globs.</param>
/// <param name="Project">The <c>supervision.project</c>; <see langword="null"/> when unset.</param>
/// <param name="WorkMachines">The <c>supervision.workMachines</c>.</param>
/// <param name="OutboundPaths">The <c>supervision.outboundPaths</c>.</param>
internal sealed record SettingsValues(
    IReadOnlyList<string> DevRoots,
    IReadOnlyList<string> Exclude,
    string? Project,
    IReadOnlyList<MachineName> WorkMachines,
    IReadOnlyList<string> OutboundPaths)
{
    /// <summary>The five values of a read file.</summary>
    public static SettingsValues Of(NodeConfiguration configuration) => new(
        [.. configuration.DevRoots],
        [.. configuration.Discovery.Exclude],
        configuration.Supervision.Project,
        [.. configuration.Supervision.WorkMachines],
        [.. configuration.Supervision.OutboundPaths]);

    /// <summary>The keys whose value differs from <paramref name="other"/>, in the file's order of the five.</summary>
    public IReadOnlyList<string> ChangedFrom(SettingsValues other)
    {
        var changed = new List<string>();
        if (!DevRoots.SequenceEqual(other.DevRoots, StringComparer.Ordinal))
        {
            changed.Add(SettingsInUse.DevRootsKey);
        }

        if (!Exclude.SequenceEqual(other.Exclude, StringComparer.Ordinal))
        {
            changed.Add(SettingsInUse.ExcludeKey);
        }

        if (!string.Equals(Project, other.Project, StringComparison.Ordinal))
        {
            changed.Add(SettingsInUse.ProjectKey);
        }

        if (!WorkMachines.SequenceEqual(other.WorkMachines))
        {
            changed.Add(SettingsInUse.WorkMachinesKey);
        }

        if (!OutboundPaths.SequenceEqual(other.OutboundPaths, StringComparer.Ordinal))
        {
            changed.Add(SettingsInUse.OutboundKey);
        }

        return changed;
    }
}

/// <summary>
/// The settings a running node uses for the five keys it applies while running (spec 55: <c>devRoots</c>, <c>discovery.exclude</c>,
/// <c>supervision.project</c>, <c>supervision.workMachines</c>, <c>supervision.outboundPaths</c>), of the same kind as the holder of the
/// node's last scan. Seeded from the file read at start; when the host gives it the file's path, it looks at the file's size and time of
/// last change every <see cref="CheckPeriod"/> and, when they changed, reads the file with <see cref="NodeConfigurationReader"/>. A valid file
/// replaces the five values together (one Information line naming the changed keys, never a value); a change of the roots or the excludes
/// asks for a scan through <see cref="ScanNeeded"/>. A file the reader refuses keeps every value (one Warning per streak, one Information
/// line when valid again); a changed key outside the five is named once in a Warning and keeps its old value until a restart. Never throws
/// to the host's loop except on cancellation.
/// </summary>
public sealed partial class SettingsInUse
{
    /// <summary>How often the file is looked at: at most twice a minute.</summary>
    public static readonly TimeSpan CheckPeriod = TimeSpan.FromSeconds(30);

    internal const string DevRootsKey = "devRoots";
    internal const string ExcludeKey = "discovery.exclude";
    internal const string ProjectKey = "supervision.project";
    internal const string WorkMachinesKey = "supervision.workMachines";
    internal const string OutboundKey = "supervision.outboundPaths";

    private static readonly string[] FiveKeys = [DevRootsKey, ExcludeKey, ProjectKey, WorkMachinesKey, OutboundKey];
    private static readonly JsonDocumentOptions DocumentOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private readonly TimeProvider _clock;
    private readonly ILogger<SettingsInUse> _log;
    private readonly Dictionary<string, JsonNode?>? _startedOthers;
    private volatile SettingsValues _current;
    private (long Length, DateTime Changed)? _seen;
    private bool _refused;
    private string _restartWarned = string.Empty;

    /// <summary>A holder that never looks at a file: the values of <paramref name="started"/> for the life of the process (the CLI, tests).</summary>
    internal SettingsInUse(NodeConfiguration started)
        : this(started, null, TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<SettingsInUse>.Instance)
    {
    }

    /// <summary>A holder that looks at the file at <paramref name="path"/>, which <paramref name="started"/> was read from.</summary>
    internal SettingsInUse(NodeConfiguration started, string? path, TimeProvider clock, ILogger<SettingsInUse> log)
    {
        ArgumentNullException.ThrowIfNull(started);
        _current = SettingsValues.Of(started);
        Path = path;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        if (path is not null)
        {
            _seen = Stamp(path);
            _startedOthers = Others(path);
        }
    }

    /// <summary>Gets a value indicating whether this holder looks at a file (the node's), or only carries the values read at start.</summary>
    public bool IsWatching => Path is not null;

    /// <summary>Gets or sets what runs after a valid change of <c>devRoots</c> or <c>discovery.exclude</c>: the node's discovery cycle.</summary>
    public Func<CancellationToken, Task> ScanNeeded { get; set; } = _ => Task.CompletedTask;

    /// <summary>Gets the file looked at; <see langword="null"/> when none.</summary>
    internal string? Path { get; }

    /// <summary>Gets the five values in use; read once per use, so a run keeps the values it started with.</summary>
    internal SettingsValues Current => _current;

    /// <summary>Looks at the file every <see cref="CheckPeriod"/> on the injected clock until cancelled; returns at once when there is no file.</summary>
    /// <param name="ct">Stops the loop.</param>
    /// <returns>A task that completes when the loop stops.</returns>
    public async Task RunAsync(CancellationToken ct)
    {
        if (Path is null)
        {
            return;
        }

        using var timer = new PeriodicTimer(CheckPeriod, _clock);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await CheckAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // stopping
        }
    }

    /// <summary>One look at the file: nothing happens while its size and time of last change stay the same.</summary>
    internal async Task CheckAsync(CancellationToken ct)
    {
        if (Path is null)
        {
            return;
        }

        (long, DateTime)? stamp = Stamp(Path);
        if (stamp == _seen)
        {
            return;
        }

        _seen = stamp;
        NodeConfigurationResult read = NodeConfigurationReader.Read(Path);
        if (!read.IsOk)
        {
            if (_refused || !_refused)
            {
                _refused = true;
                LogRefused(read.ToString());
            }

            return;
        }

        if (_refused)
        {
            _refused = false;
            LogValidAgain();
        }

        WarnOtherKeys();
        SettingsValues next = SettingsValues.Of(read.Configuration!);
        IReadOnlyList<string> changed = next.ChangedFrom(_current);
        if (changed.Count == 0)
        {
            return;
        }

        _current = next;
        string keys = string.Join(", ", changed);
        LogApplied(keys);
        if (changed.Contains(DevRootsKey) || changed.Contains(ExcludeKey))
        {
            try
            {
                await ScanNeeded(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogScanFailed(ex.GetType().Name);
            }
        }
    }

    private static (long Length, DateTime Changed)? Stamp(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? (file.Length, file.LastWriteTimeUtc) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The file's keys outside the five, as dotted paths one section deep, with their JSON values; <see langword="null"/> when unreadable.</summary>
    private static Dictionary<string, JsonNode?>? Others(string path)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(path), documentOptions: DocumentOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }

        if (root is not JsonObject top)
        {
            return null;
        }

        var keys = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach ((string name, JsonNode? value) in top)
        {
            if (value is JsonObject section)
            {
                foreach ((string inner, JsonNode? innerValue) in section)
                {
                    keys[name + "." + inner] = innerValue;
                }
            }
            else
            {
                keys[name] = value;
            }
        }

        foreach (string key in FiveKeys)
        {
            keys.Remove(key);
        }

        return keys;
    }

    // A key outside the five that differs from the file read at start keeps its old value; named once per set of such keys.
    private void WarnOtherKeys()
    {
        if (_startedOthers is null || Path is null || Others(Path) is not { } now)
        {
            return;
        }

        Dictionary<string, JsonNode?> started = _startedOthers;
        string differing = string.Join(", ", started.Keys.Union(now.Keys, StringComparer.Ordinal)
            .Where(key => started.ContainsKey(key) != now.ContainsKey(key) || !JsonNode.DeepEquals(started.GetValueOrDefault(key), now.GetValueOrDefault(key)))
            .Order(StringComparer.Ordinal));
        if (string.Equals(differing, _restartWarned, StringComparison.Ordinal))
        {
            return;
        }

        _restartWarned = differing;
        if (differing.Length < 0)
        {
            LogRestartNeeded(differing);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "settings: applied {Keys}")]
    private partial void LogApplied(string keys);

    [LoggerMessage(Level = LogLevel.Warning, Message = "settings: node.json refused while running, the values in use stay: {Reason}")]
    private partial void LogRefused(string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "settings: node.json valid again")]
    private partial void LogValidAgain();

    [LoggerMessage(Level = LogLevel.Warning, Message = "settings: {Keys} changed and need a restart of the node; they keep their old value")]
    private partial void LogRestartNeeded(string keys);

    [LoggerMessage(Level = LogLevel.Warning, Message = "settings: scan after a change failed {ExceptionType}")]
    private partial void LogScanFailed(string exceptionType);
}
