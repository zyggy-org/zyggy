using System.CommandLine;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Zyggy.Core.Dream;

namespace Zyggy.Cli.Commands;

/// <summary>
/// <c>zyggy dream [--trigger nightly|on-demand|manual]</c>, <c>zyggy dream request</c> and <c>zyggy dream status [--json]</c>
/// (spec 28 CLI surface). The run is the Core <see cref="DreamRunner"/>; this file only wires configuration, pin, logging and exit codes.
/// </summary>
internal static class DreamCommand
{
    private const string RequestFile = "dream.request";

    public static Command Create(CliEnvironment environment)
    {
        var trigger = new Option<string>("--trigger")
        {
            Description = "nightly, on-demand or manual (default manual)",
            DefaultValueFactory = _ => "manual",
        };
        trigger.AcceptOnlyFromAmong("nightly", "on-demand", "manual");
        var dream = new Command("dream", "File the waiting facts into long-term memory, commit and push.") { trigger };
        dream.SetAction((parseResult, cancellationToken) => RunAsync(environment, parseResult.GetValue(trigger)!, cancellationToken));

        var request = new Command("request", "Ask for a dream run now (the path unit starts it).");
        request.SetAction((_, _) => Task.FromResult(Request(environment)));
        dream.Subcommands.Add(request);

        var json = new Option<bool>("--json") { Description = "Print the last run record as JSON." };
        var status = new Command("status", "Print the last dream run.") { json };
        status.SetAction((parseResult, _) => Task.FromResult(Status(environment, parseResult.GetValue(json))));
        dream.Subcommands.Add(status);
        return dream;
    }

    private static async Task<int> RunAsync(CliEnvironment environment, string triggerText, CancellationToken cancellationToken)
    {
        var version = InformationalVersion();
        var configuration = DreamConfiguration.Load(environment.Variables, version, path => File.Exists(path) ? File.ReadAllText(path) : null);
        if (configuration.Environment is not { } dreamEnvironment || configuration.Options is not { } options)
        {
            return ConfigurationError(configuration.Error!);
        }

        if (configuration.PinJson is { } pin)
        {
            var binary = Environment.ProcessPath ?? string.Empty;
            var hash = File.Exists(binary) ? Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(binary, cancellationToken))) : string.Empty;
            var check = VersionPin.Check(pin, version, hash, RuntimeInformation.RuntimeIdentifier);
            if (check.Status != VersionPinStatus.Match)
            {
                return ConfigurationError("version_mismatch: " + check.Detail);
            }
        }

        DreamTriggerWire.TryFromWire(triggerText, out var trigger);
        var request = Path.Join(dreamEnvironment.StateDirectory, RequestFile);
        if (File.Exists(request))
        {
            File.Delete(request);
            trigger = DreamTrigger.OnDemand;
        }

        var claude = environment.Get("ZYGGY_CLAUDE_PATH");
        await using var services = new ServiceCollection()
            .AddLogging(logging => logging.AddSystemdConsole())
            .AddZyggyDream(dreamEnvironment, options, model => model.Path = claude ?? "claude")
            .BuildServiceProvider();
        var record = await services.GetRequiredService<DreamRunner>().RunAsync(trigger, cancellationToken).ConfigureAwait(false);
        return DreamExitCode.From(record);
    }

    private static int Request(CliEnvironment environment)
    {
        var state = StateDirectory(environment);
        Directory.CreateDirectory(state);
        var path = Path.Join(state, RequestFile);
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (new FileStream(path, options))
        {
        }

        Console.Out.WriteLine("dream requested");
        return ExitCodes.Ok;
    }

    private static int Status(CliEnvironment environment, bool json)
    {
        var record = DreamRunRecordStore.ReadLast(StateDirectory(environment));
        if (record is null)
        {
            Console.Out.WriteLine("no dream run yet");
            return ExitCodes.NoRun;
        }

        if (json)
        {
            Console.Out.WriteLine(DreamRunRecordStore.Serialize(record));
            return ExitCodes.Ok;
        }

        var why = record.Check ?? record.Reason;
        var ended = (record.Ended ?? record.Started).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{ended} {record.Trigger} {record.Outcome}{(why is null ? "" : $" ({why})")}: batches {record.Batches.Count}, lines {record.Batches.Sum(b => b.Lines)}, " +
            $"quarantined {record.Quarantined}, remaining {record.InboxRemaining.Lines}, cost {record.CostUsdTotal:0.00} USD, " +
            $"commit {(record.Commit is { Length: >= 7 } sha ? sha[..7] : "-")}, pushed {(record.Pushed ? "yes" : "no")}"));
        return ExitCodes.Ok;
    }

    private static string StateDirectory(CliEnvironment environment) =>
        environment.Get("ZYGGY_STATE_DIR")
        ?? Path.Join(environment.Get("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state", "zyggy");

    private static int ConfigurationError(string message)
    {
        Console.Error.WriteLine("zyggy: configuration error: " + message);
        return ExitCodes.Configuration;
    }

    private static string InformationalVersion() =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
}
