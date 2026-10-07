using Zyggy.Core.LinkedIn.Mcp;
using Zyggy.Core.Verbs;

namespace Zyggy.Core.LinkedIn;

/// <summary>
/// <c>zyggy linkedin mcp-server</c> (spec 36 CLI surface): MCP over the process's stdin and stdout until stdin closes. Exit 0 · 3
/// (configuration) · 4 (any argument) · 5 (an unattended run, <c>ZYGGY_HOOKS=off</c>), each refusal as one stderr line.
/// </summary>
internal sealed class McpServerVerb(LinkedInVerbContext context) : ILinkedInVerb
{
    public async Task<int> RunAsync(IReadOnlyList<string> args, VerbIo io, CancellationToken cancellationToken)
    {
        if (args.Count > 0)
        {
            await io.Error.WriteAsync("linkedin: mcp-server takes no arguments\n").ConfigureAwait(false);
            return 4;
        }

        var start = LinkedInServerStart.Decide(context.Environment, context.FindTimeZone);
        if (start.Exit is { } exit)
        {
            await io.Error.WriteAsync(start.Message + "\n").ConfigureAwait(false);
            return exit;
        }

        await using var stdin = Console.OpenStandardInput();
        await using var stdout = Console.OpenStandardOutput();
        return await LinkedInMcpServer.RunAsync(stdin, stdout, start, () => PublishToolFactory.Create(context, io.Error), cancellationToken)
            .ConfigureAwait(false);
    }
}
