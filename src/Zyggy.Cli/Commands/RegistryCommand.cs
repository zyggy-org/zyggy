using System.CommandLine;
using System.Text;

using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;
using Zyggy.Core.Registry;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Verbs;

namespace Zyggy.Cli.Commands;

/// <summary>
/// <c>zyggy registry [--machine &lt;name&gt;]</c> (spec 15 CLI): the tenant's published registry files from the local bus checkout, one line per
/// project, for the owner and the <c>delegate</c> skill. Each file is read with <see cref="RegistryDocument.Parse"/>; registry text is printed
/// as data, never interpreted. Read-only: no pull, no lock, nothing written.
/// </summary>
internal static class RegistryCommand
{
    /// <summary>The longest description a line carries (spec 15).</summary>
    internal const int MaxDescription = 120;

    private const string None = "-";

    public static Command Create(CliEnvironment environment, VerbIo io, Action<IServiceCollection>? configure)
    {
        var machine = new Option<string?>("--machine") { Description = "Show only this machine's projects." };
        var command = new Command("registry", "List the projects every machine of the tenant published to the bus registry.") { machine, BusHost.ConfigOption, BusHost.VerboseOption };
        command.SetAction((parseResult, ct) => RunAsync(environment, io, configure, parseResult, parseResult.GetValue(machine), ct));
        return command;
    }

    private static async Task<int> RunAsync(CliEnvironment environment, VerbIo io, Action<IServiceCollection>? configure, ParseResult parseResult, string? machineText, CancellationToken ct)
    {
        MachineName? only = null;
        if (machineText is not null && !MachineName.TryParse(machineText, out only))
        {
            await io.Error.WriteLineAsync("zyggy: registry: --machine must be a machine name");
            return ExitCodes.Usage;
        }

        (NodeConfiguration? configuration, int exitCode) = BusHost.LoadConfiguration(parseResult, environment, io);
        if (configuration is null)
        {
            return exitCode;
        }

        await using ServiceProvider services = BusHost.BuildServices(configuration, parseResult.GetValue(BusHost.VerboseOption), environment, configure);
        int checkout = await BusHost.CheckCheckoutAsync(services, configuration, io, ct);
        if (checkout != ExitCodes.Ok)
        {
            return checkout;
        }

        string relativeFolder = BusPaths.RegistryFolder(configuration.Tenant);
        string folder = BusPaths.Resolve(configuration.Bus.LocalPath, relativeFolder);
        var files = new List<(MachineName Machine, string Path)>();
        if (Directory.Exists(folder))
        {
            foreach (string file in Directory.EnumerateFiles(folder))
            {
                if (BusPaths.TryParse(relativeFolder + "/" + Path.GetFileName(file), out BusLocation? location)
                    && location is { Folder: BusFolder.Registry, Machine: { } machine }
                    && (only is null || machine == only))
                {
                    files.Add((machine, file));
                }
            }
        }

        if (files.Count == 0)
        {
            await io.Out.WriteLineAsync(only is null
                ? $"no registry file in tenant {configuration.Tenant.Value}"
                : $"no registry file for {only.Value} in tenant {configuration.Tenant.Value}");
            return ExitCodes.NotFound;
        }

        DateTimeOffset now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        TimeSpan staleAfter = TimeSpan.FromHours(configuration.Discovery.LastSeenHours);
        var sb = new StringBuilder();
        foreach ((MachineName machine, string path) in files.OrderBy(f => f.Machine.Value, StringComparer.Ordinal))
        {
            RegistryParse parse = RegistryDocument.Parse(await File.ReadAllBytesAsync(path, ct));
            if (parse.Document is not { } document || document.Tenant != configuration.Tenant || document.Machine != machine)
            {
                string field = parse.Field ?? (parse.Document!.Tenant != configuration.Tenant ? "tenant" : "machine");
                await io.Error.WriteLineAsync($"zyggy: registry: {Path.GetFileName(path)} unreadable ({field})");
                continue;
            }

            Render(sb, document, now, staleAfter);
        }

        await io.Out.WriteAsync(sb.ToString());
        return ExitCodes.Ok;
    }

    /// <summary>
    /// One machine's lines: <c>&lt;machine&gt;[ (stale)] &lt;project&gt; default:&lt;branch|-&gt; agents:&lt;a,b|-&gt; — &lt;description|-&gt;</c> per project, ordinal by
    /// project, or <c>&lt;machine&gt;[ (stale)] (no projects)</c>. Stale: <c>last_seen</c> absent or older than <paramref name="staleAfter"/>.
    /// </summary>
    internal static void Render(StringBuilder sb, RegistryDocument document, DateTimeOffset now, TimeSpan staleAfter)
    {
        bool stale = document.LastSeen is not { } seen || now - seen > staleAfter;
        string machine = document.Machine.Value + (stale ? " (stale)" : string.Empty);
        if (document.Projects.Count == 0)
        {
            sb.Append(machine).Append(" (no projects)\n");
            return;
        }

        foreach (RegistryProject project in document.Projects.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            sb.Append(machine).Append(' ').Append(project.Name)
                .Append(" default:").Append(project.DefaultBranch ?? None)
                .Append(" agents:").Append(project.Agents.Count > 0 ? string.Join(",", project.Agents.Select(a => a.Name)) : None)
                .Append(" — ").Append(Description(project.Summary))
                .Append('\n');
        }
    }

    private static string Description(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return None;
        }

        string first = summary.Split('\n')[0].TrimEnd('\r').Trim();
        return first.Length == 0 ? None : first.Length > MaxDescription ? first[..MaxDescription] : first;
    }
}
