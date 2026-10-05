using Zyggy.Core.Memory;
using Zyggy.Core.Verbs;

namespace Zyggy.Core.M365;

/// <summary>
/// <c>zyggy m365 parse &lt;file inside ZYGGY_M365_RUN_DIR&gt;</c>: the template's <c>parse.sh</c> through <see cref="DocumentParser"/>.
/// Exit 0 · 3 configuration · 4 usage · 5 refused · 6 MarkItDown failed or timed out. Linux only (exit 3 elsewhere).
/// Accepts <c>ZYGGY_HOOKS=off</c>.
/// </summary>
internal sealed class ParseVerb(M365VerbContext context) : IM365Verb
{
    private const string Usage = " (usage: zyggy m365 parse <file inside ZYGGY_M365_RUN_DIR>)";

    public async Task<int> RunAsync(IReadOnlyList<string> args, VerbIo io, CancellationToken cancellationToken)
    {
        if (args.Count == 0 || (args.Count == 1 && args[0].Length == 0))
        {
            await io.Error.WriteAsync("parse: no file given" + Usage + "\n").ConfigureAwait(false);
            return 4;
        }

        if (args.Count > 1)
        {
            await io.Error.WriteAsync("parse: one file only" + Usage + "\n").ConfigureAwait(false);
            return 4;
        }

        if (!OperatingSystem.IsLinux())
        {
            await io.Error.WriteAsync("parse: not supported on this platform\n").ConfigureAwait(false);
            return 3;
        }

        var environment = context.Environment;
        var instance = M365Environment.Load(environment);
        var parser = new DocumentParser(
            context.Runner,
            () => instance.Environment is { } m365
                ? M365Configuration.Load(m365.ConfigPath, baseOnly: false, DateOnly.FromDateTime(context.Clock.GetUtcNow().UtcDateTime), IsKnownTimeZone)
                : new M365ConfigurationLoad(null, instance.Error, null),
            name => name == "markitdown" ? ProgramLocator.UserProgram(name, environment) : ProgramLocator.OnPath(name, environment),
            () => SecretPatterns.Load(instance.Environment?.SecretPatternsPath ?? SecretPatternsLocation.Resolve(environment) ?? "secret-patterns.txt"));
        var runDirectory = environment.TryGetValue("ZYGGY_M365_RUN_DIR", out var run) ? run : null;
        var result = await parser.ParseAsync(new ParseRequest(args[0], runDirectory, Environment.CurrentDirectory), cancellationToken).ConfigureAwait(false);

        await io.Out.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (io.BinaryOut is { } binary)
        {
            await binary.WriteAsync(result.Stdout, cancellationToken).ConfigureAwait(false);
            await binary.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await io.Out.WriteAsync(System.Text.Encoding.UTF8.GetString(result.Stdout)).ConfigureAwait(false);
        }

        await io.Error.WriteAsync(result.Stderr).ConfigureAwait(false);
        return result.Exit;
    }

    private bool IsKnownTimeZone(string id)
    {
        try
        {
            _ = context.FindTimeZone(id);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }
}
