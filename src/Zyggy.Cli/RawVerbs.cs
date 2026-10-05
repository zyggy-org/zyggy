using Zyggy.Core.Memory;
using Zyggy.Core.Verbs;

namespace Zyggy.Cli;

/// <summary>
/// The verbs that replace template scripts (spec 33). They take their arguments unchanged, <c>--</c> included, so their own parsers
/// keep the scripts' usage texts and exit codes; System.CommandLine never parses them. They build no host.
/// </summary>
internal static class RawVerbs
{
    public static bool TryDispatch(string[] args, CliEnvironment environment, out Task<int> run)
    {
        if (args is ["memory", "remember", .. var rest])
        {
            run = new RememberVerb(environment.Variables, TimeProvider.System).RunAsync(rest, VerbIo.FromConsole(), CancellationToken.None);
            return true;
        }

        run = Task.FromResult(0);
        return false;
    }
}
