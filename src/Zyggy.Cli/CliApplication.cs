using System.CommandLine;

using Microsoft.Extensions.DependencyInjection;

using Zyggy.Cli.Commands;
using Zyggy.Core.Secrets;
using Zyggy.Core.Verbs;

namespace Zyggy.Cli;

/// <summary>Builds the <c>zyggy</c> root command; the single place where verbs are registered.</summary>
internal static class CliApplication
{
    public static Task<int> RunAsync(string[] args, CliEnvironment environment) => RunAsync(args, environment, VerbIo.FromConsole(), configure: null, CancellationToken.None);

    /// <summary>
    /// The whole CLI with an injected console and a service hook (spec 08 ex-Q9): the verbs of deliverable 08 write through <paramref name="io"/>
    /// and build their services with <paramref name="configure"/> applied last; the older verbs keep the process console. Parse errors are
    /// one line each on <paramref name="io"/>'s error stream with exit code 2.
    /// </summary>
    public static async Task<int> RunAsync(string[] args, CliEnvironment environment, VerbIo io, Action<IServiceCollection>? configure, CancellationToken cancellationToken)
    {
        if (RawVerbs.TryDispatch(args, environment, out var raw))
        {
            return await raw;
        }

        // Listed for --help only: RawVerbs runs it with its own argument parser.
        var remember = new Command("remember", "Keep a fact the owner stated in the memory inbox (remember skill).");
        var archive = new Command("archive", "Keep, list, remove or offload to OneDrive a project's archived file beside its facts (zyggy memory archive add|list|remove|offload ...).");
        var memory = new Command("memory", "Read the owner's memory.") { MemoryDigestCommand.Create(environment), remember, archive };
        var m365 = new Command("m365", "Microsoft 365 on Central: state, facts and the connector's tools (zyggy m365 <verb> ...).");
        var brief = new Command("brief", "The morning brief, shown when the owner asks for it (zyggy brief show [--full] [<date>]); zyggy brief request asks the unit for a run now.");
        var linkedin = new Command("linkedin", "LinkedIn on Central: connect (auth start|finish|status) and the publishing server (mcp-server).");
        var hub = new Command("hub", "The memory MCP server over stdio (get_context, remember); started by Claude Code from .mcp.json, never by hand.");
        var root = new RootCommand("Zyggy: the personal agent platform command line.")
        {
            memory, DreamCommand.Create(environment), m365, brief, linkedin, hub, ProbeCommand.Create(environment, configure), SecretCommand.Create(environment, io, configure),
            SubmitCommand.Create(environment, io, configure), ContextCommand.Create(environment, io, configure), StatusCommand.Create(environment, io, configure), VerifyCommand.Create(environment, io, configure),
        };
        var parseResult = root.Parse(args);
        if (parseResult.Errors.Count > 0)
        {
            foreach (var error in parseResult.Errors)
            {
                await io.Error.WriteLineAsync(error.Message);
            }

            return ExitCodes.Usage;
        }

        // The root's own --version and --help write through the invocation configuration (plan 08 A4). The default exception
        // handler is replaced so that a secret store that cannot be opened ends every verb the same way (plan 09 A3), and any
        // other fault still prints as the default handler did and exits 1.
        try
        {
            return await parseResult.InvokeAsync(new InvocationConfiguration { Output = io.Out, Error = io.Error, EnableDefaultExceptionHandler = false }, cancellationToken);
        }
        catch (SecretStoreUnavailableException ex)
        {
            await io.Error.WriteLineAsync("zyggy: secret store unavailable: " + ex.Message);
            return ExitCodes.Failed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await io.Error.WriteLineAsync("Unhandled exception: " + ex);
            return 1;
        }
    }
}
