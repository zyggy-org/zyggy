using System.CommandLine;

using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Outlook;
using Zyggy.Core.Verbs;

namespace Zyggy.Cli.Commands;

/// <summary>
/// <c>zyggy outlook policy | count | list | read</c> (spec 58 slice 2): the work mailbox's read tools on the work laptop. Argument
/// parsing, the policy check and the reads are Core's <see cref="OutlookVerbHost"/>; this class gives it the console and, in tests,
/// the Outlook access and clock the service hook registers.
/// </summary>
internal static class OutlookCommand
{
    /// <summary>The <c>--help</c> entry; <see cref="RunAsync"/> runs the verb with its own parser.</summary>
    public static Command Create() =>
        new("outlook", "The work mailbox on the work laptop, read through classic Outlook (zyggy outlook policy | count | list | read | brief | brief-done | drafts ...); refuses without the policy record; never saves anything in Outlook.");

    /// <summary>Runs the verb with the arguments after <c>outlook</c>.</summary>
    public static async Task<int> RunAsync(IReadOnlyList<string> args, CliEnvironment environment, VerbIo io, Action<IServiceCollection>? configure, CancellationToken ct)
    {
        if (configure is null)
        {
            return await new OutlookVerbHost(environment.Variables).RunAsync(args, io, ct);
        }

        var probe = new ServiceCollection();
        configure(probe);
        await using ServiceProvider configured = probe.BuildServiceProvider();
        return await OutlookVerbHost.FromServices(configured, environment.Variables).RunAsync(args, io, ct);
    }
}
