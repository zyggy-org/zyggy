using System.CommandLine;

using Zyggy.Core.Configuration;
using Zyggy.Core.Models;
using Zyggy.Core.Verbs;

namespace Zyggy.Cli.Commands;

/// <summary>
/// <c>zyggy models</c> (spec 50, CLI): prints the allow-list, one line per job type, the supervisor's model and where they were read —
/// Central's <c>instance/models.json</c>, else this machine's <c>node.json</c> <c>claude.models</c>. Exit 0; an invalid table exits 2 with
/// one line naming the key. Reading and the text are Core's (<see cref="ModelTable"/>, <see cref="ModelChoice.Describe"/>).
/// </summary>
internal static class ModelsCommand
{
    public static Command Create(CliEnvironment environment, VerbIo io)
    {
        var command = new Command("models", "Show the models jobs may run on: the allow-list, the model of each job type and of the supervisor run (instance/models.json, else node.json claude.models).")
        {
            BusHost.ConfigOption,
        };
        command.SetAction((parseResult, _) => RunAsync(environment, io, parseResult));
        return command;
    }

    /// <summary>Central's table as the verbs read it: no file → no table; an invalid file → its one line, printed by the caller with exit 2.</summary>
    public static ModelTableResult LoadTable(CliEnvironment environment) => ModelTable.Load(ModelTable.PathIn(environment.Variables), ModelTable.ReadFileOrNull);

    private static async Task<int> RunAsync(CliEnvironment environment, VerbIo io, ParseResult parseResult)
    {
        ModelTableResult table = LoadTable(environment);
        if (table.Error is { } error)
        {
            await io.Error.WriteLineAsync("zyggy: " + error);
            return ExitCodes.Usage;
        }

        IReadOnlyList<string>? nodeModels = null;
        if (table.Table is null)
        {
            (NodeConfiguration? configuration, int exitCode) = BusHost.LoadConfiguration(parseResult, environment, io);
            if (configuration is null)
            {
                return exitCode;
            }

            nodeModels = configuration.Claude.Models;
        }

        await io.Out.WriteAsync(ModelChoice.Describe(table.Table, nodeModels));
        return ExitCodes.Ok;
    }
}
