using System.CommandLine;

using Zyggy.Cli.Commands;

namespace Zyggy.Cli;

/// <summary>Builds the <c>zyggy</c> root command; the single place where verbs are registered.</summary>
internal static class CliApplication
{
    public static async Task<int> RunAsync(string[] args, CliEnvironment environment)
    {
        if (RawVerbs.TryDispatch(args, environment, out var raw))
        {
            return await raw;
        }

        // Listed for --help only: RawVerbs runs it with its own argument parser.
        var remember = new Command("remember", "Keep a fact the owner stated in the memory inbox (remember skill).");
        var archive = new Command("archive", "Keep, list or remove a project's archived file beside its facts (zyggy memory archive add|list|remove ...).");
        var memory = new Command("memory", "Read the owner's memory.") { MemoryDigestCommand.Create(environment), remember, archive };
        var m365 = new Command("m365", "Microsoft 365 on Central: state, facts and the connector's tools (zyggy m365 <verb> ...).");
        var brief = new Command("brief", "The morning brief, shown when the owner asks for it (zyggy brief show [--full] [<date>]); zyggy brief request asks the unit for a run now.");
        var linkedin = new Command("linkedin", "LinkedIn on Central: connect (auth start|finish|status) and the publishing server (mcp-server).");
        var root = new RootCommand("Zyggy: the personal agent platform command line.") { memory, DreamCommand.Create(environment), m365, brief, linkedin };
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
