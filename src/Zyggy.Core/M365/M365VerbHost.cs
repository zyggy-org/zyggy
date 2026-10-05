using Zyggy.Core.Processes;
using Zyggy.Core.Verbs;

namespace Zyggy.Core.M365;

/// <summary>
/// What every <c>zyggy m365</c> verb is given: the environment, the clock, the time-zone lookup, the process runner; for tests, the Graph
/// handler (the stub; <see langword="null"/> is the real network) and whether the key file's Unix mode and owner are checked.
/// </summary>
internal sealed record M365VerbContext(
    IReadOnlyDictionary<string, string?> Environment,
    TimeProvider Clock,
    Func<string, TimeZoneInfo> FindTimeZone,
    IProcessRunner Runner,
    HttpMessageHandler? GraphHandler = null,
    bool CheckKeyOwnership = true);

/// <summary>One <c>zyggy m365 &lt;verb&gt;</c>: the template script it replaces, with that script's arguments, texts and exit codes.</summary>
internal interface IM365Verb
{
    Task<int> RunAsync(IReadOnlyList<string> args, VerbIo io, CancellationToken cancellationToken);
}

/// <summary>
/// <c>zyggy m365 &lt;verb&gt; …</c> (spec 33): dispatches to the verb that replaces a template script. The verb gets its arguments
/// unchanged. An unknown verb exits 4. Builds no generic host.
/// </summary>
public sealed class M365VerbHost
{
    private const string Usage = " (usage: zyggy m365 <verb> …)";

    private readonly M365VerbContext _context;
    private readonly Dictionary<string, Func<M365VerbContext, IM365Verb>> _verbs = new(StringComparer.Ordinal)
    {
        ["state"] = context => new StateVerb(context),
        ["facts"] = context => new FactsVerb(context),
        ["parse"] = context => new ParseVerb(context),
        ["check"] = context => new CheckVerb(context),
        ["token-test"] = context => new TokenTestVerb(context),
        ["cert-init"] = context => new CertInitVerb(context),
        ["guard"] = context => new GuardVerb(context),
        ["log"] = context => new LogVerb(context),
    };

    /// <summary>Creates the host over the process environment and the system clock.</summary>
    /// <param name="environment">The process environment.</param>
    public M365VerbHost(IReadOnlyDictionary<string, string?> environment)
        : this(environment, TimeProvider.System, TimeZoneInfo.FindSystemTimeZoneById, new ProcessRunner(TimeProvider.System))
    {
    }

    // Tests: a fake clock, a process runner, the stubbed Graph handler, and a zone lookup (IANA ids need ICU off Linux, and the build is invariant-globalization).
    internal M365VerbHost(
        IReadOnlyDictionary<string, string?> environment,
        TimeProvider clock,
        Func<string, TimeZoneInfo> findTimeZone,
        IProcessRunner runner,
        HttpMessageHandler? graphHandler = null,
        bool checkKeyOwnership = true)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(findTimeZone);
        ArgumentNullException.ThrowIfNull(runner);
        _context = new M365VerbContext(environment, clock, findTimeZone, runner, graphHandler, checkKeyOwnership);
    }

    /// <summary>Runs <c>m365 &lt;verb&gt; …</c>.</summary>
    /// <param name="args">The arguments after <c>m365</c>.</param>
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
            await io.Error.WriteAsync("m365: no verb given" + Usage + "\n").ConfigureAwait(false);
            return 4;
        }

        if (!_verbs.TryGetValue(args[0], out var create))
        {
            await io.Error.WriteAsync($"m365: unknown verb '{ShellText.Prefix(args[0], 40)}'{Usage}\n").ConfigureAwait(false);
            return 4;
        }

        return await create(_context).RunAsync(args.Skip(1).ToList(), io, cancellationToken).ConfigureAwait(false);
    }
}
