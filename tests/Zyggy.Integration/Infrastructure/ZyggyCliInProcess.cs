using Microsoft.Extensions.DependencyInjection;

using Zyggy.Cli;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Integration.Infrastructure;

/// <summary>
/// Runs the <c>zyggy</c> CLI in process (spec 08 ex-Q9): the whole root command with an injected console and a service hook, so a verb
/// can be driven against the bare repository with the harness's edges (doorbell, recorded processes) and its output asserted exactly.
/// </summary>
internal static class ZyggyCliInProcess
{
    public static async Task<(int Exit, VerbConsole Console)> RunAsync(
        string[] args,
        IReadOnlyDictionary<string, string?> environment,
        Action<IServiceCollection>? configure = null,
        string? stdin = null,
        CancellationToken ct = default)
    {
        var console = new VerbConsole(stdin);
        int exit = await CliApplication.RunAsync(args, new CliEnvironment(environment), console.Io, configure, ct);
        return (exit, console);
    }
}
