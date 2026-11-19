using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Zyggy.Core.Bus;
using Zyggy.Core.Memory;
using Zyggy.Core.Setup;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Verbs;

namespace Zyggy.Core.Configuration;

/// <summary>A project a scan with given settings finds: its registry name and its folder.</summary>
/// <param name="Name">The registry name.</param>
/// <param name="Path">The project folder.</param>
public sealed record SettingsProject(string Name, string Path);

/// <summary>
/// <c>zyggy settings show | set | add | remove</c> (spec 55): reads, and on Central changes, the five settings a running node applies —
/// <c>devRoots</c>, <c>discovery.exclude</c>, <c>supervision.project</c>, <c>supervision.workMachines</c>, <c>supervision.outboundPaths</c>.
/// A closed list of keys, each value checked by its rule; the file is written as <c>zyggy setup</c> writes it (a temp file checked by
/// <see cref="NodeConfigurationReader"/>, a backup, then the replacement), every other key left as it was. Exit 0 done or unchanged,
/// 2 an unknown key, a wrong form or a refused value, 3 not Central, a file the reader refuses or absent memory variables, 5 an
/// unattended run (<c>ZYGGY_HOOKS=off</c>). <c>show</c> works everywhere, also unattended. Builds no host.
/// </summary>
public sealed class SettingsVerb
{
    /// <summary>The exit code of a change, an unchanged value or a <c>show</c>.</summary>
    public const int Ok = 0;

    /// <summary>The exit code of an unknown key, a wrong form or a value that fails its check.</summary>
    public const int Refused = 2;

    /// <summary>The exit code of a machine that is not Central, a file the reader refuses, or absent memory variables.</summary>
    public const int Configuration = 3;

    /// <summary>The exit code of a change tried in an unattended run.</summary>
    public const int Unattended = 5;

    private const string DevRoots = "devRoots";
    private const string Exclude = "discovery.exclude";
    private const string Project = "supervision.project";
    private const string WorkMachines = "supervision.workMachines";
    private const string Outbound = "supervision.outboundPaths";
    private const string None = "(none)";
    private const string Usage = "usage: zyggy settings show | set <key> <value> | add <key> <value> [--exclude <glob>]... | remove <key> <value> [--config <path>]";

    private static readonly string[] Keys = [DevRoots, Exclude, Project, WorkMachines, Outbound];
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static readonly JsonDocumentOptions DocumentOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly IReadOnlyDictionary<string, string?> _environment;
    private readonly TimeProvider _clock;
    private readonly Func<NodeConfiguration, CancellationToken, Task<IReadOnlyList<SettingsProject>>> _scan;

    /// <summary>Initializes a new instance of the <see cref="SettingsVerb"/> class.</summary>
    /// <param name="environment">The process environment.</param>
    /// <param name="clock">The clock for the backup's name.</param>
    /// <param name="scan">Finds the projects a discovery scan with the given settings publishes on this machine.</param>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    public SettingsVerb(
        IReadOnlyDictionary<string, string?> environment,
        TimeProvider clock,
        Func<NodeConfiguration, CancellationToken, Task<IReadOnlyList<SettingsProject>>> scan)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(scan);
        _environment = environment;
        _clock = clock;
        _scan = scan;
    }

    /// <summary>Gets a test hook run on the changed file's JSON before it is written to the temp file.</summary>
    internal Action<JsonObject>? BeforeWrite { get; init; }

    /// <summary>Runs the verb.</summary>
    /// <param name="args">The arguments after <c>settings</c>.</param>
    /// <param name="io">The console.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The exit code.</returns>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    public async Task<int> RunAsync(IReadOnlyList<string> args, VerbIo io, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(io);

        if (!TryParse(args, out Arguments? parsed))
        {
            return await FailAsync(io, Refused, Usage).ConfigureAwait(false);
        }

        string path = NodeConfigurationReader.ResolvePath(parsed.Config, _environment);
        if (parsed.Form == "show")
        {
            return await ShowAsync(path, io).ConfigureAwait(false);
        }

        if (_environment.TryGetValue("ZYGGY_HOOKS", out string? hooks) && hooks == "off")
        {
            return await FailAsync(io, Unattended, "refused: settings are changed only in an attended session").ConfigureAwait(false);
        }

        NodeConfigurationResult read = NodeConfigurationReader.Read(path);
        if (!read.IsOk)
        {
            return await FailAsync(io, Configuration, read.ToString()).ConfigureAwait(false);
        }

        NodeConfiguration configuration = read.Configuration!;
        if (configuration.Machine != MachineName.Central)
        {
            return await FailAsync(io, Configuration, "settings are changed by this verb only on Central").ConfigureAwait(false);
        }

        if (FormRefusal(parsed) is { } formRefusal)
        {
            return await FailAsync(io, Refused, formRefusal).ConfigureAwait(false);
        }

        Five before = Five.Of(configuration);
        Check check = await CheckAsync(parsed, before, configuration, cancellationToken).ConfigureAwait(false);
        if (check.Exit != Ok)
        {
            return await FailAsync(io, check.Exit, check.Message!).ConfigureAwait(false);
        }

        Five after = check.After!;
        string[] named = parsed.Key == DevRoots && parsed.Excludes.Count > 0 ? [DevRoots, Exclude] : [parsed.Key];
        string[] changed = [.. named.Where(key => !before.Text(key).Equals(after.Text(key), StringComparison.Ordinal))];
        if (changed.Length == 0)
        {
            foreach (string key in named)
            {
                await io.Out.WriteAsync($"settings: {key}: unchanged\n").ConfigureAwait(false);
            }

            return Ok;
        }

        NodeConfiguration result = after.Apply(configuration);
        IReadOnlyList<SettingsProject>? found = null;
        if (after.Project is { } project && changed.Any(key => key is DevRoots or Exclude))
        {
            found = await _scan(result, cancellationToken).ConfigureAwait(false);
            if (!found.Any(p => p.Name == project))
            {
                return await FailAsync(io, Refused, $"supervision.project {project} would no longer be found").ConfigureAwait(false);
            }
        }

        if (Write(path, changed, after) is { } writeRefusal)
        {
            return await FailAsync(io, Configuration, writeRefusal).ConfigureAwait(false);
        }

        var output = new StringBuilder();
        foreach (string key in named)
        {
            output.Append(CultureInfo.InvariantCulture, $"settings: {key}: ")
                .Append(changed.Contains(key) ? before.Text(key) + " -> " + after.Text(key) : "unchanged").Append('\n');
        }

        if (parsed.Form == "remove" && parsed.Key == WorkMachines && changed.Length > 0)
        {
            output.Append(CultureInfo.InvariantCulture, $"settings: {parsed.Value} is no longer a work machine: its questions are answered without the read root\n");
        }

        if (parsed.Form == "add" && parsed.Key == DevRoots)
        {
            found ??= await _scan(result, cancellationToken).ConfigureAwait(false);
            string[] publishes = [.. found.Where(p => IsAtOrUnder(p.Path, parsed.Value)).Select(p => p.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
            output.Append("settings: publishes: ").Append(publishes.Length == 0 ? "nothing" : string.Join(", ", publishes)).Append('\n');
        }

        output.Append("settings: the node applies this within a minute\n");
        await io.Out.WriteAsync(output.ToString()).ConfigureAwait(false);
        return Ok;
    }

    private static async Task<int> ShowAsync(string path, VerbIo io)
    {
        NodeConfigurationResult read = NodeConfigurationReader.Read(path);
        if (!read.IsOk)
        {
            return await FailAsync(io, Configuration, read.ToString()).ConfigureAwait(false);
        }

        Five five = Five.Of(read.Configuration!);
        var output = new StringBuilder();
        foreach (string key in Keys)
        {
            output.Append(key).Append(": ").Append(five.Text(key)).Append('\n');
        }

        await io.Out.WriteAsync(output.ToString()).ConfigureAwait(false);
        return Ok;
    }

    private static string? FormRefusal(Arguments parsed)
    {
        if (!Keys.Contains(parsed.Key, StringComparer.Ordinal))
        {
            return "unknown setting: " + parsed.Key;
        }

        if (parsed.Key == Project && parsed.Form != "set")
        {
            return Project + " takes set";
        }

        if (parsed.Key != Project && parsed.Form == "set")
        {
            return parsed.Key + " takes add or remove";
        }

        if (parsed.Excludes.Count > 0 && (parsed.Form != "add" || parsed.Key != DevRoots))
        {
            return "--exclude goes only with add devRoots";
        }

        return null;
    }

    /// <summary>Checks the value by its key's rule and returns the five settings after the change, or the refusal.</summary>
    private async Task<Check> CheckAsync(Arguments parsed, Five before, NodeConfiguration configuration, CancellationToken cancellationToken)
    {
        string value = parsed.Value;
        bool add = parsed.Form == "add";
        switch (parsed.Key)
        {
            case Project:
                {
                    IReadOnlyList<SettingsProject> found = await _scan(configuration, cancellationToken).ConfigureAwait(false);
                    if (!found.Any(p => p.Name == value))
                    {
                        string names = found.Count == 0 ? "none" : string.Join(", ", found.Select(p => p.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
                        return Check.Refuse(Refused, $"no such project on this machine: {value} (projects: {names})");
                    }

                    return Check.To(before with { Project = value });
                }

            case DevRoots:
                {
                    if (add && (!Path.IsPathFullyQualified(value) || !Directory.Exists(value)))
                    {
                        return Check.Refuse(Refused, "no such folder: " + value);
                    }

                    if (parsed.Excludes.FirstOrDefault(glob => !IsFolderGlob(glob)) is { } badGlob)
                    {
                        return Check.Refuse(Refused, "not a folder-name glob: " + badGlob);
                    }

                    Five after = before with { DevRoots = Change(before.DevRoots, value, add, PathComparison) };
                    foreach (string glob in parsed.Excludes)
                    {
                        after = after with { Exclude = Change(after.Exclude, glob, add: true, StringComparison.Ordinal) };
                    }

                    return Check.To(after);
                }

            case Exclude:
                if (add && !IsFolderGlob(value))
                {
                    return Check.Refuse(Refused, "not a folder-name glob: " + value);
                }

                return Check.To(before with { Exclude = Change(before.Exclude, value, add, StringComparison.Ordinal) });

            case WorkMachines:
                if (add)
                {
                    if (!MachineName.TryParse(value, out MachineName? machine))
                    {
                        return Check.Refuse(Refused, "no such machine on the bus: " + value);
                    }

                    if (machine == MachineName.Central)
                    {
                        return Check.Refuse(Refused, "central is never a work machine");
                    }

                    if (!File.Exists(Path.Join(configuration.Bus.LocalPath, BusPaths.Registry(configuration.Tenant, machine))))
                    {
                        return Check.Refuse(Refused, "no such machine on the bus: " + value);
                    }

                    if (before.Outbound.Count == 0)
                    {
                        return Check.Refuse(Refused, "name an outbound folder first");
                    }
                }

                return Check.To(before with { WorkMachines = Change(before.WorkMachines, value, add, StringComparison.Ordinal) });

            default:
                return CheckOutbound(value, add, before);
        }
    }

    private Check CheckOutbound(string value, bool add, Five before)
    {
        if (!add)
        {
            IReadOnlyList<string> remaining = Change(before.Outbound, value, add: false, PathComparison);
            return remaining.Count == 0 && before.WorkMachines.Count > 0
                ? Check.Refuse(Refused, "the last outbound folder stays while supervision.workMachines is not empty")
                : Check.To(before with { Outbound = remaining });
        }

        MemoryEnvironmentResult memory = MemoryEnvironment.Resolve(_environment);
        if (memory.Error is not null)
        {
            return Check.Refuse(Configuration, memory.Error);
        }

        if (!Path.IsPathFullyQualified(value) || !Directory.Exists(value))
        {
            return Check.Refuse(Refused, "no such folder: " + value);
        }

        string business = Path.Join(memory.Paths!.PrincipalDirectory, "business");
        if (!Directory.Exists(business) || !IsAtOrUnder(ResolveLinks(value), ResolveLinks(business)))
        {
            return Check.Refuse(Refused, "outbound folders lie under the business memory");
        }

        return Check.To(before with { Outbound = Change(before.Outbound, value, add: true, PathComparison) });
    }

    /// <summary>Writes the changed keys into the file: temp file, reader check, backup, replacement. Returns the refusal, or null.</summary>
    private string? Write(string path, IReadOnlyList<string> changed, Five after)
    {
        byte[] original;
        JsonObject root;
        try
        {
            original = File.ReadAllBytes(path);
            ReadOnlySpan<byte> json = original.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? original.AsSpan(Encoding.UTF8.Preamble.Length) : original;
            if (JsonNode.Parse(json, nodeOptions: null, DocumentOptions) is not JsonObject parsed)
            {
                return "node.json must be a JSON object";
            }

            root = parsed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return "node.json could not be read: " + ex.Message;
        }

        foreach (string key in changed)
        {
            JsonNode node = key == Project ? JsonValue.Create(after.Project!) : new JsonArray([.. after.Values(key).Select(v => (JsonNode?)JsonValue.Create(v))]);
            int dot = key.IndexOf('.', StringComparison.Ordinal);
            if (dot < 0)
            {
                root[key] = node;
                continue;
            }

            string section = key[..dot];
            if (root[section] is not JsonObject sectionObject)
            {
                sectionObject = [];
                root[section] = sectionObject;
            }

            sectionObject[key[(dot + 1)..]] = node;
        }

        BeforeWrite?.Invoke(root);
        string stamp = _clock.GetUtcNow().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        string temp = SetupPaths.Temp(path, stamp);
        try
        {
            File.WriteAllText(temp, root.ToJsonString(WriteOptions) + "\n", Utf8);
            NodeConfigurationResult written = NodeConfigurationReader.Read(temp);
            if (!written.IsOk)
            {
                File.Delete(temp);
                return written.ToString();
            }

            File.WriteAllBytes(SetupPaths.Backup(path, stamp), original);
            File.Move(temp, path, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // Our own temp file only; the original was never replaced.
            }

            return "node.json could not be written: " + ex.Message;
        }
    }

    private static IReadOnlyList<string> Change(IReadOnlyList<string> list, string value, bool add, StringComparison comparison)
    {
        bool present = list.Any(item => Same(item, value, comparison));
        if (add)
        {
            return present ? list : [.. list, value];
        }

        return present ? [.. list.Where(item => !Same(item, value, comparison))] : list;
    }

    private static bool Same(string a, string b, StringComparison comparison) =>
        comparison == StringComparison.Ordinal
            ? string.Equals(a, b, comparison)
            : string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), comparison);

    private static bool IsFolderGlob(string glob) => glob.Trim().Length > 0 && glob.IndexOfAny(['/', '\\']) < 0;

    private static bool IsAtOrUnder(string path, string folder)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return full.Equals(parent, PathComparison)
            || full.StartsWith(parent + Path.DirectorySeparatorChar, PathComparison)
            || full.StartsWith(parent + Path.AltDirectorySeparatorChar, PathComparison);
    }

    /// <summary>The path with every link along it resolved to its final target, segment by segment.</summary>
    private static string ResolveLinks(string path)
    {
        string full = Path.GetFullPath(path);
        string root = Path.GetPathRoot(full) ?? string.Empty;
        string current = root;
        foreach (string part in full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Join(current, part);
            var info = new DirectoryInfo(current);
            if (info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
            {
                current = Path.GetFullPath(target.FullName);
            }
        }

        return Path.TrimEndingDirectorySeparator(current);
    }

    private static async Task<int> FailAsync(VerbIo io, int exit, string message)
    {
        await io.Error.WriteAsync("zyggy: " + message + "\n").ConfigureAwait(false);
        return exit;
    }

    private static bool TryParse(IReadOnlyList<string> args, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Arguments? parsed)
    {
        parsed = null;
        string? config = null;
        var excludes = new List<string>();
        var positional = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] is "--config" or "--exclude")
            {
                if (i + 1 >= args.Count)
                {
                    return false;
                }

                if (args[i] == "--config")
                {
                    config = args[++i];
                }
                else
                {
                    excludes.Add(args[++i]);
                }

                continue;
            }

            positional.Add(args[i]);
        }

        bool ok = positional.Count switch
        {
            1 => positional[0] == "show" && excludes.Count == 0,
            3 => positional[0] is "set" or "add" or "remove",
            _ => false,
        };
        if (!ok)
        {
            return false;
        }

        parsed = positional.Count == 1
            ? new Arguments("show", string.Empty, string.Empty, excludes, config)
            : new Arguments(positional[0], positional[1], positional[2], excludes, config);
        return true;
    }

    private sealed record Arguments(string Form, string Key, string Value, IReadOnlyList<string> Excludes, string? Config);

    private sealed record Check(int Exit, string? Message, Five? After)
    {
        public static Check To(Five after) => new(Ok, null, after);

        public static Check Refuse(int exit, string message) => new(exit, message, null);
    }

    /// <summary>The five settings a running node applies, as plain values.</summary>
    private sealed record Five(IReadOnlyList<string> DevRoots, IReadOnlyList<string> Exclude, string? Project, IReadOnlyList<string> WorkMachines, IReadOnlyList<string> Outbound)
    {
        public static Five Of(NodeConfiguration configuration) => new(
            configuration.DevRoots,
            configuration.Discovery.Exclude,
            configuration.Supervision.Project,
            [.. configuration.Supervision.WorkMachines.Select(m => m.Value)],
            configuration.Supervision.OutboundPaths);

        public IReadOnlyList<string> Values(string key) => key switch
        {
            SettingsVerb.DevRoots => DevRoots,
            SettingsVerb.Exclude => Exclude,
            SettingsVerb.WorkMachines => WorkMachines,
            SettingsVerb.Outbound => Outbound,
            _ => Project is null ? [] : [Project],
        };

        public string Text(string key) => Values(key) is { Count: > 0 } values ? string.Join(", ", values) : None;

        public NodeConfiguration Apply(NodeConfiguration configuration) => configuration with
        {
            DevRoots = DevRoots,
            Discovery = configuration.Discovery with { Exclude = Exclude },
            Supervision = configuration.Supervision with
            {
                Project = Project,
                WorkMachines = [.. WorkMachines.Select(MachineName.Parse)],
                OutboundPaths = Outbound,
            },
        };
    }
}
