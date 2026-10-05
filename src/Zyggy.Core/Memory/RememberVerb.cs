using System.Text.RegularExpressions;

using Zyggy.Core.Verbs;

namespace Zyggy.Core.Memory;

/// <summary>
/// <c>zyggy memory remember [--scope general|project:&lt;name&gt;|machine] [--tag stated|observed] [--source &lt;text&gt;] -- &lt;fact…&gt;</c>:
/// the template's <c>remember.sh</c> in .NET (spec 33 AC-8..AC-10). Exit 0 kept, 2 refused (secret pattern), 3 configuration
/// error, 4 usage; <c>ZYGGY_HOOKS=off</c> → 0 with no output. Never runs git. Builds no host.
/// </summary>
/// <param name="environment">The process environment.</param>
/// <param name="clock">The clock for the local date.</param>
public sealed class RememberVerb(IReadOnlyDictionary<string, string?> environment, TimeProvider clock)
{
    private readonly Func<string, TimeZoneInfo> _findTimeZone = TimeZoneInfo.FindSystemTimeZoneById;

    // Tests: IANA ids need ICU off Linux, and the build is invariant-globalization.
    internal RememberVerb(IReadOnlyDictionary<string, string?> environment, TimeProvider clock, Func<string, TimeZoneInfo> findTimeZone)
        : this(environment, clock) => _findTimeZone = findTimeZone;

    /// <summary>The exit code of a kept fact.</summary>
    public const int Kept = 0;

    /// <summary>The exit code of a fact or source refused by a secret pattern.</summary>
    public const int Refused = 2;

    /// <summary>The exit code of a configuration error.</summary>
    public const int Configuration = 3;

    /// <summary>The exit code of a usage error.</summary>
    public const int Usage = 4;

    private const string Prefix = "remember: ";

    /// <summary>Runs the verb.</summary>
    /// <param name="args">The arguments after <c>memory remember</c>, unchanged (<c>--</c> included).</param>
    /// <param name="io">The console.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The exit code.</returns>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    public async Task<int> RunAsync(IReadOnlyList<string> args, VerbIo io, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(io);

        if (environment.TryGetValue("ZYGGY_HOOKS", out var hooks) && hooks == "off")
        {
            return Kept;
        }

        var memory = MemoryEnvironment.Resolve(environment, _findTimeZone);
        if (memory.Error is not null)
        {
            await io.Error.WriteAsync(Prefix + "configuration error: " + memory.Error + "\n").ConfigureAwait(false);
            return Configuration;
        }

        var parsed = RememberArguments.Parse(args);
        if (parsed.Error is not null)
        {
            await io.Error.WriteAsync(Prefix + parsed.Error + RememberArguments.UsageSuffix + "\n").ConfigureAwait(false);
            return Usage;
        }

        var location = SecretPatternsLocation.Resolve(environment);
        var load = location is null
            ? new SecretPatternsLoad(null, "ZYGGY_SECRET_PATTERNS is not set (and no ZYGGY_INSTANCE_DIR or CLAUDE_PROJECT_DIR to derive it from)")
            : SecretPatterns.Load(location);
        if (load.Patterns is null)
        {
            await io.Error.WriteAsync(Prefix + "configuration error: " + load.Error + "\n").ConfigureAwait(false);
            return Configuration;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var outcome = new RememberService(memory.Paths!, memory.TimeZone!, clock, load.Patterns).Remember(parsed.Request!);
        if (outcome.RefusedBy is not null)
        {
            await io.Error.WriteAsync($"refused: matches secret pattern {outcome.RefusedBy}\n").ConfigureAwait(false);
            return Refused;
        }

        await io.Out.WriteAsync($"remembered: {outcome.Path}\n{outcome.Line}\n").ConfigureAwait(false);
        return Kept;
    }
}

/// <summary>The argument loop and checks of <c>remember.sh</c>, in the script's order and with its messages.</summary>
internal static partial class RememberArguments
{
    public const string UsageSuffix =
        " (usage: zyggy memory remember [--scope general|project:<name>|machine] [--tag stated|observed] [--source <text>] -- \"<fact>\")";

    public static (RememberRequest? Request, string? Error) Parse(IReadOnlyList<string> args)
    {
        var scope = "general";
        var tag = "stated";
        var source = string.Empty;
        string? fact = null;
        for (var i = 0; i < args.Count && fact is null; i++)
        {
            switch (args[i])
            {
                case "--scope" or "--tag" or "--source":
                    if (i + 1 >= args.Count)
                    {
                        return Fail($"{args[i]} needs a value");
                    }

                    var value = args[i + 1];
                    _ = args[i] switch
                    {
                        "--scope" => scope = value,
                        "--tag" => tag = value,
                        _ => source = TextCollapse.Line(value),
                    };
                    i++;
                    break;
                case "--":
                    fact = string.Join(' ', args.Skip(i + 1));
                    break;
                default:
                    return Fail($"unexpected argument '{args[i]}'");
            }
        }

        if (fact is null)
        {
            return Fail("the fact must follow --");
        }

        fact = TextCollapse.Line(fact);
        if (fact.Length == 0)
        {
            return Fail("empty fact");
        }

        if (TextCollapse.CharCount(fact) > 1000)
        {
            return Fail("fact longer than 1000 characters");
        }

        string hint;
        if (scope == "general")
        {
            hint = string.Empty;
        }
        else if (scope == "machine")
        {
            hint = " (machine)";
        }
        else if (scope.StartsWith("project:", StringComparison.Ordinal))
        {
            if (!ProjectName().IsMatch(scope["project:".Length..]))
            {
                return Fail("invalid project name in --scope");
            }

            hint = $" ({scope})";
        }
        else
        {
            return Fail("--scope must be general, project:<name> or machine");
        }

        if (tag is not ("stated" or "observed"))
        {
            return Fail("--tag must be stated or observed");
        }

        if (tag == "observed" && source.Length == 0)
        {
            return Fail("--tag observed needs --source");
        }

        return (new RememberRequest(tag, hint, source, fact), null);
    }

    private static (RememberRequest?, string?) Fail(string message) => (null, message);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ProjectName();
}
