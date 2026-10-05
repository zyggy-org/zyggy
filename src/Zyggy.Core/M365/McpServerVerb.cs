using Zyggy.Core.M365.Mcp;
using Zyggy.Core.M365.Tools;
using Zyggy.Core.Processes;
using Zyggy.Core.Verbs;

namespace Zyggy.Core.M365;

/// <summary>
/// <c>zyggy m365 mcp-server [--probe]</c>: <c>mcp-server.sh</c> (spec 23 D8, spec 33 AC-25, AC-26). Without <c>--probe</c> this process is
/// replaced by the reviewed server (<c>execve</c>): org mode on loopback HTTP, exactly the ten variables, no token. With <c>--probe</c> the
/// running server is checked. Exit 0 · 3 configuration or server installation · 4 usage · 6 a probe the server failed. Linux only.
/// </summary>
internal sealed class McpServerVerb(M365VerbContext context, IProcessReplacer? replacer = null) : IM365Verb
{
    private const string Prefix = "m365: ";

    public async Task<int> RunAsync(IReadOnlyList<string> args, VerbIo io, CancellationToken cancellationToken)
    {
        var probe = false;
        if (args.Count > 0)
        {
            if (args[0] != "--probe" || args.Count != 1)
            {
                return await FailAsync(io, 4, $"unexpected argument '{args[0]}' (usage: zyggy m365 mcp-server [--probe])").ConfigureAwait(false);
            }

            probe = true;
        }

        if (!probe && !OperatingSystem.IsLinux())
        {
            return await FailAsync(io, 3, "not supported on this platform").ConfigureAwait(false);
        }

        var (session, exit, error) = M365Session.Load(context, baseOnly: false);
        if (session is null)
        {
            return await FailAsync(io, exit, error!).ConfigureAwait(false);
        }

        if (session.Warning is not null)
        {
            await io.Error.WriteAsync(session.Warning + "\n").ConfigureAwait(false);
        }

        if (M365ToolPartition.Load(session.Instance.Checkout) is not { Partition: { } partition } load)
        {
            return await FailAsync(io, 3, M365ToolPartition.Load(session.Instance.Checkout).Error!).ConfigureAwait(false);
        }

        if (probe)
        {
            if (M365Environment.Port(context.Environment, out var portError) is not { } port)
            {
                return await FailAsync(io, 3, portError!).ConfigureAwait(false);
            }

            using var graph = session.CreateGraph(context);
            var helper = new HeaderHelper(graph.Tokens, context.Runner, context.Clock, ProgramLocator.OnPath("logger", context.Environment));
            var outcome = await new McpProbe(helper, partition.Enabled, port).RunAsync(cancellationToken).ConfigureAwait(false);
            if (outcome.Error is not null)
            {
                return await FailAsync(io, outcome.Exit, outcome.Error).ConfigureAwait(false);
            }

            await io.Out.WriteAsync(outcome.Stdout).ConfigureAwait(false);
            return 0;
        }

        var plan = McpServerLaunch.Plan(context.Environment, session.Instance, session.Configuration, partition);
        if (plan.Plan is not { } launch)
        {
            return await FailAsync(io, plan.Exit, plan.Error!).ConfigureAwait(false);
        }

        // download-bytes-to-file writes into the callers' run directories under the shared download root.
        if (McpServerLaunch.EnsureDownloadRoot(session.Instance.Paths.DownloadRoot) is { } rootError)
        {
            return await FailAsync(io, 3, rootError).ConfigureAwait(false);
        }

        await io.Out.FlushAsync(cancellationToken).ConfigureAwait(false);
        await io.Error.FlushAsync(cancellationToken).ConfigureAwait(false);
        var reason = (replacer ?? new PosixProcessReplacer()).Exec(launch.ServerPath, launch.Argv, launch.Environment);
        return await FailAsync(io, 3, $"cannot start {launch.ServerPath}: {reason}").ConfigureAwait(false);
    }

    private static async Task<int> FailAsync(VerbIo io, int exit, string message)
    {
        await io.Error.WriteAsync(Prefix + message + "\n").ConfigureAwait(false);
        return exit;
    }
}
