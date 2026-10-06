using System.Globalization;

using Zyggy.Core.M365;
using Zyggy.Core.Verbs;

namespace Zyggy.Core.Brief;

/// <summary>What every <c>zyggy brief</c> verb is given: the environment, the clock and the time-zone lookup (tests: a custom zone).</summary>
internal sealed record BriefVerbContext(IReadOnlyDictionary<string, string?> Environment, TimeProvider Clock, Func<string, TimeZoneInfo> FindTimeZone);

/// <summary>
/// <c>zyggy brief show [--full] [&lt;YYYY-MM-DD&gt;]</c> (spec 35 AC-12, AC-56..AC-61, AC-67): prints the brief the owner asked for, fenced as
/// data, then records <c>last-shown</c> — only after stdout was flushed without error. Exit 0 printed (also "no brief for", "not ready",
/// the failure line) · 3 configuration, state directory missing or a brief unreadable · 4 usage. Never reads a mailbox or a model.
/// </summary>
internal sealed class BriefShowVerb(BriefVerbContext context)
{
    private const string Prefix = "brief: ";
    private const string Usage = " (usage: zyggy brief show [--full] [<YYYY-MM-DD>])";

    public async Task<int> RunAsync(IReadOnlyList<string> args, VerbIo io, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(io);
        var full = false;
        DateOnly? date = null;
        foreach (var arg in args)
        {
            if (arg == "--full" && !full)
            {
                full = true;
            }
            else if (date is null && !arg.StartsWith('-')
                && DateOnly.TryParseExact(arg, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                date = parsed;
            }
            else
            {
                await io.Error.WriteAsync($"{Prefix}unexpected argument '{ShellText.Prefix(arg, 40)}'{Usage}\n").ConfigureAwait(false);
                return 4;
            }
        }

        var zoneId = Value("ZYGGY_TIMEZONE");
        if (zoneId is null)
        {
            return await FailAsync(io, 3, "configuration error: ZYGGY_TIMEZONE is not set").ConfigureAwait(false);
        }

        TimeZoneInfo zone;
        try
        {
            zone = zoneId == "UTC" ? TimeZoneInfo.Utc : context.FindTimeZone(zoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return await FailAsync(io, 3, $"configuration error: ZYGGY_TIMEZONE '{ShellText.Prefix(zoneId, 40)}' is not a known time zone").ConfigureAwait(false);
        }

        var (settings, error) = BriefSettings.Load(context.Environment);
        if (settings is null)
        {
            return await FailAsync(io, 3, error!).ConfigureAwait(false);
        }

        var paths = new BriefPaths(context.Environment);
        if (!Directory.Exists(paths.Directory))
        {
            return await FailAsync(io, 3, $"{paths.Directory} is missing — runbook 13 \"Brief run failed\"").ConfigureAwait(false);
        }

        var store = new BriefStore(paths);
        ShowOutcome outcome;
        try
        {
            outcome = new BriefShow(store, settings, zone, context.Clock, new M365Paths(context.Environment)).Decide(date, full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var path = ex is FileNotFoundException { FileName: { } f } ? f : paths.Directory;
            return await FailAsync(io, 3, $"{path} cannot be read — runbook 13 \"Brief run failed\"").ConfigureAwait(false);
        }

        try
        {
            await io.Out.WriteAsync(outcome.Text).ConfigureAwait(false);
            await io.Out.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // stdout gone (a closed pipe): nothing was shown, so nothing is recorded.
            return 3;
        }

        if (outcome.NewLastShown is { } shown)
        {
            try
            {
                store.WriteLastShown(shown);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or StateDirectoryException)
            {
                await io.Error.WriteAsync($"{Prefix}could not write {paths.LastShown}; the brief was printed\n").ConfigureAwait(false);
            }
        }

        return 0;
    }

    private string? Value(string key) => context.Environment.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value) ? value : null;

    private static async Task<int> FailAsync(VerbIo io, int exit, string message)
    {
        await io.Error.WriteAsync(Prefix + message + "\n").ConfigureAwait(false);
        return exit;
    }
}

/// <summary>
/// <c>zyggy brief &lt;verb&gt; …</c> (spec 35): dispatches to the verb with its arguments unchanged. An unknown verb exits 4. Builds no host.
/// </summary>
public sealed class BriefVerbHost
{
    private const string Usage = " (usage: zyggy brief show [--full] [<YYYY-MM-DD>])";

    private readonly BriefVerbContext _context;

    /// <summary>Creates the host over the process environment and the system clock.</summary>
    /// <param name="environment">The process environment.</param>
    public BriefVerbHost(IReadOnlyDictionary<string, string?> environment)
        : this(environment, TimeProvider.System, TimeZoneInfo.FindSystemTimeZoneById)
    {
    }

    // Tests: a fake clock and a zone lookup (IANA ids need ICU off Linux, and the build is invariant-globalization).
    internal BriefVerbHost(IReadOnlyDictionary<string, string?> environment, TimeProvider clock, Func<string, TimeZoneInfo> findTimeZone)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(findTimeZone);
        _context = new BriefVerbContext(environment, clock, findTimeZone);
    }

    /// <summary>Runs <c>brief &lt;verb&gt; …</c>.</summary>
    /// <param name="args">The arguments after <c>brief</c>.</param>
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
            await io.Error.WriteAsync("brief: no verb given" + Usage + "\n").ConfigureAwait(false);
            return 4;
        }

        if (args[0] != "show")
        {
            await io.Error.WriteAsync($"brief: unknown verb '{ShellText.Prefix(args[0], 40)}'{Usage}\n").ConfigureAwait(false);
            return 4;
        }

        return await new BriefShowVerb(_context).RunAsync(args.Skip(1).ToList(), io, cancellationToken).ConfigureAwait(false);
    }
}
