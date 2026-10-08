using Zyggy.Core.Verbs;

namespace Zyggy.Core.LinkedIn;

/// <summary>
/// <c>zyggy linkedin &lt;verb&gt; …</c> (spec 36): <c>auth start</c>, <c>auth finish</c>, <c>auth status</c> and <c>mcp-server</c>. An
/// unknown verb exits 4. Builds no generic host.
/// </summary>
public sealed class LinkedInVerbHost
{
    private const string Usage = " (usage: zyggy linkedin <auth start|auth finish|auth status|mcp-server>)";

    private readonly LinkedInVerbContext _context;
    private readonly Dictionary<string, Func<LinkedInVerbContext, ILinkedInVerb>> _verbs = new(StringComparer.Ordinal)
    {
        ["auth start"] = context => new AuthStartVerb(context),
        ["auth finish"] = context => new AuthFinishVerb(context),
        ["auth status"] = context => new AuthStatusVerb(context),
        ["mcp-server"] = context => new McpServerVerb(context),
    };

    /// <summary>Creates the host over the process environment and the system clock.</summary>
    /// <param name="environment">The process environment.</param>
    public LinkedInVerbHost(IReadOnlyDictionary<string, string?> environment)
        : this(environment, TimeProvider.System, TimeZoneInfo.FindSystemTimeZoneById)
    {
    }

    // Tests: a fake clock, a zone lookup (IANA ids need ICU off Linux), the stubbed LinkedIn handler, the ownership check and the HTTP timeout.
    internal LinkedInVerbHost(
        IReadOnlyDictionary<string, string?> environment,
        TimeProvider clock,
        Func<string, TimeZoneInfo> findTimeZone,
        HttpMessageHandler? linkedInHandler = null,
        bool checkOwnership = true,
        TimeSpan? httpTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(findTimeZone);
        _context = new LinkedInVerbContext(environment, clock, findTimeZone, linkedInHandler, checkOwnership, httpTimeout);
    }

    /// <summary>The <c>publish_post</c> handler over this host's context (the MCP server's, and the integration tests' in process).</summary>
    internal IPublishTool CreatePublishTool(TextWriter diagnostics) => PublishToolFactory.Create(_context, diagnostics);

    /// <summary>Runs <c>linkedin &lt;verb&gt; …</c>.</summary>
    /// <param name="args">The arguments after <c>linkedin</c>.</param>
    /// <param name="io">The console.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The verb's exit code; 4 for an unknown verb.</returns>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    public async Task<int> RunAsync(IReadOnlyList<string> args, VerbIo io, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(io);
        if (args.Count == 0)
        {
            await io.Error.WriteAsync("linkedin: no verb given" + Usage + "\n").ConfigureAwait(false);
            return 4;
        }

        // "auth" takes its sub-verb as part of the name; every other verb is one word.
        var words = args[0] == "auth" && args.Count > 1 ? 2 : 1;
        var verb = string.Join(' ', args.Take(words));
        if (!_verbs.TryGetValue(verb, out var create))
        {
            await io.Error.WriteAsync($"linkedin: unknown verb '{ShellText.Prefix(verb, 40)}'{Usage}\n").ConfigureAwait(false);
            return 4;
        }

        return await create(_context).RunAsync(args.Skip(words).ToList(), io, cancellationToken).ConfigureAwait(false);
    }
}
