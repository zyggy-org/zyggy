using Zyggy.Core.Brief;
using Zyggy.Core.LinkedIn;
using Zyggy.Core.M365;
using Zyggy.Core.Memory;
using Zyggy.Core.Verbs;

namespace Zyggy.Cli;

/// <summary>
/// The verbs that replace template scripts (spec 33): <c>memory remember</c> and <c>m365 …</c>; <c>memory archive …</c> (spec 37); the morning brief's <c>brief …</c> (spec 35); and <c>linkedin …</c> (spec 36). They take their arguments unchanged, <c>--</c> included, so their own parsers
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

        if (args is ["memory", "archive", .. var archive])
        {
            run = new ArchiveVerb(environment.Variables, TimeProvider.System).RunAsync(archive, VerbIo.FromConsole(), CancellationToken.None);
            return true;
        }

        if (args is ["m365", .. var m365])
        {
            run = new M365VerbHost(environment.Variables).RunAsync(m365, VerbIo.FromConsole(), CancellationToken.None);
            return true;
        }

        if (args is ["brief", .. var brief])
        {
            run = new BriefVerbHost(environment.Variables).RunAsync(brief, VerbIo.FromConsole(), CancellationToken.None);
            return true;
        }

        if (args is ["linkedin", .. var linkedin])
        {
            run = new LinkedInVerbHost(environment.Variables).RunAsync(linkedin, VerbIo.FromConsole(), CancellationToken.None);
            return true;
        }

        run = Task.FromResult(0);
        return false;
    }
}
