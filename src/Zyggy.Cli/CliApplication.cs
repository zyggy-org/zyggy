using System.CommandLine;

using Zyggy.Cli.Commands;

namespace Zyggy.Cli;

/// <summary>Builds the <c>zyggy</c> root command; the single place where verbs are registered.</summary>
internal static class CliApplication
{
    public static async Task<int> RunAsync(string[] args, CliEnvironment environment)
    {
        var memory = new Command("memory", "Read the owner's memory.") { MemoryDigestCommand.Create(environment) };
        var root = new RootCommand("Zyggy: the personal agent platform command line.") { memory, DreamCommand.Create(environment) };
        var parseResult = root.Parse(args);
        if (parseResult.Errors.Count > 0)
        {
            foreach (var error in parseResult.Errors)
            {
                await Console.Error.WriteLineAsync(error.Message);
            }

            return ExitCodes.Usage;
        }

        return await parseResult.InvokeAsync();
    }
}
