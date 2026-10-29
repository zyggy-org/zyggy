using Zyggy.Core.Verbs;

namespace Zyggy.Core.Hub;

/// <summary>
/// <c>zyggy hub</c>: the memory MCP server over the process's stdin and stdout until stdin closes (spec 11, D5). Started by Claude Code
/// from <c>.mcp.json</c>, never by hand. Exit 0 stdin closed, 3 configuration error, 4 any argument (<c>--proxy</c> is deliverable 21).
/// Every refusal is one stderr line; stdout carries only protocol lines. Builds no host.
/// </summary>
/// <param name="environment">The process environment.</param>
/// <param name="clock">The clock for dates and the <c>generated</c> attribute.</param>
public sealed class HubVerb(IReadOnlyDictionary<string, string?> environment, TimeProvider clock)
{
    private readonly Func<string, TimeZoneInfo> _findTimeZone = TimeZoneInfo.FindSystemTimeZoneById;

    // Tests: IANA ids need ICU off Linux, and the build is invariant-globalization.
    internal HubVerb(IReadOnlyDictionary<string, string?> environment, TimeProvider clock, Func<string, TimeZoneInfo> findTimeZone)
        : this(environment, clock) => _findTimeZone = findTimeZone;

    /// <summary>The exit code when stdin closes.</summary>
    public const int StdinClosed = 0;

    /// <summary>The exit code of a configuration error (principal, memory directory, time zone, secret patterns).</summary>
    public const int Configuration = 3;

    /// <summary>The exit code of any argument.</summary>
    public const int Usage = 4;

    /// <summary>Runs the verb.</summary>
    /// <param name="args">The arguments after <c>hub</c>; there must be none.</param>
    /// <param name="io">The console; only its error stream is written.</param>
    /// <param name="cancellationToken">Cancels the server.</param>
    /// <returns>The exit code.</returns>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    public async Task<int> RunAsync(IReadOnlyList<string> args, VerbIo io, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(io);
        if (args.Count > 0)
        {
            await io.Error.WriteAsync(args.Contains("--proxy")
                ? "hub: --proxy is deliverable 21; zyggy hub takes no arguments\n"
                : "hub: takes no arguments\n").ConfigureAwait(false);
            return Usage;
        }

        var start = HubStartup.Decide(environment, _findTimeZone);
        if (start.Exit is { } exit)
        {
            await io.Error.WriteAsync(start.Message + "\n").ConfigureAwait(false);
            return exit;
        }

        await using var stdin = Console.OpenStandardInput();
        await using var stdout = Console.OpenStandardOutput();
        return await HubServer.RunAsync(stdin, stdout, io.Error, start, clock, cancellationToken).ConfigureAwait(false);
    }
}
