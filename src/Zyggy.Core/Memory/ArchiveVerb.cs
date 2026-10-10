using System.Globalization;

using Zyggy.Core.Verbs;

namespace Zyggy.Core.Memory;

/// <summary>
/// <c>zyggy memory archive add | list | remove</c> (spec 37): keeps a file the owner hands over in a session as a project archive item
/// with a sidecar and one <c>[stated]</c> index line. Exit 0 archived, 2 refused (<see cref="ArchiveRefusal"/>), 3 configuration error,
/// 4 usage, 6 git error, 7 committed but push deferred. <c>ZYGGY_HOOKS=off</c> refuses <c>add</c> and <c>remove</c> before anything is read.
/// Builds no host.
/// </summary>
/// <param name="environment">The process environment.</param>
/// <param name="clock">The clock for the local date.</param>
public sealed class ArchiveVerb(IReadOnlyDictionary<string, string?> environment, TimeProvider clock)
{
    /// <summary>The exit code of an archived or removed item.</summary>
    public const int Archived = 0;

    /// <summary>The exit code of a refusal.</summary>
    public const int Refused = 2;

    /// <summary>The exit code of a configuration error.</summary>
    public const int Configuration = 3;

    /// <summary>The exit code of a usage error.</summary>
    public const int Usage = 4;

    /// <summary>The exit code of a git failure.</summary>
    public const int GitError = 6;

    /// <summary>The exit code of a committed item whose push was deferred.</summary>
    public const int PushDeferred = 7;

    private const string Prefix = "archive: ";

    private readonly Func<string, TimeZoneInfo> _findTimeZone = TimeZoneInfo.FindSystemTimeZoneById;

    // Tests: IANA ids need ICU off Linux, and the build is invariant-globalization.
    internal ArchiveVerb(IReadOnlyDictionary<string, string?> environment, TimeProvider clock, Func<string, TimeZoneInfo> findTimeZone)
        : this(environment, clock) => _findTimeZone = findTimeZone;

    /// <summary>Runs the verb.</summary>
    /// <param name="args">The arguments after <c>memory archive</c>.</param>
    /// <param name="io">The console.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The exit code.</returns>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    public async Task<int> RunAsync(IReadOnlyList<string> args, VerbIo io, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(io);

        var verb = args.Count > 0 ? args[0] : string.Empty;
        if (verb is not ("add" or "list" or "remove"))
        {
            await io.Error.WriteAsync($"{Prefix}unknown verb '{verb}' (usage: zyggy memory archive add|list|remove ...)\n").ConfigureAwait(false);
            return Usage;
        }

        if (verb is not "list" && environment.TryGetValue("ZYGGY_HOOKS", out var hooks) && hooks == "off")
        {
            await io.Error.WriteAsync(Prefix + "refused: unattended run\n").ConfigureAwait(false);
            return Refused;
        }

        var memory = MemoryEnvironment.Resolve(environment, _findTimeZone);
        if (memory.Error is not null)
        {
            return await ConfigurationErrorAsync(io, memory.Error).ConfigureAwait(false);
        }

        var location = SecretPatternsLocation.Resolve(environment);
        var load = location is null
            ? new SecretPatternsLoad(null, "ZYGGY_SECRET_PATTERNS is not set (and no ZYGGY_INSTANCE_DIR or CLAUDE_PROJECT_DIR to derive it from)")
            : SecretPatterns.Load(location);
        if (load.Patterns is null)
        {
            return await ConfigurationErrorAsync(io, load.Error!).ConfigureAwait(false);
        }

        var configuration = ArchiveConfiguration.Load(environment, path => File.Exists(path) ? File.ReadAllText(path) : null);
        if (configuration.Options is null)
        {
            return await ConfigurationErrorAsync(io, configuration.Error!).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var rest = args.Skip(1).ToArray();
        return verb switch
        {
            "add" => await AddAsync(rest, io, new ArchiveContext(memory.Paths!, memory.TimeZone!, load.Patterns, configuration.Options)).ConfigureAwait(false),
            "remove" => await RemoveAsync(rest, io).ConfigureAwait(false),
            _ => await ListAsync(rest, io).ConfigureAwait(false),
        };
    }

    private async Task<int> AddAsync(string[] args, VerbIo io, ArchiveContext context)
    {
        var (request, error) = ArchiveArguments.ParseAdd(args);
        if (request is null)
        {
            await io.Error.WriteAsync(Prefix + error + ArchiveArguments.UsageSuffixAdd + "\n").ConfigureAwait(false);
            return Usage;
        }

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), context.TimeZone).DateTime);
        var longest = ArchiveIndexLine.Longest(today, request);
        if (longest.Length > ArchiveIndexLine.MaxLength)
        {
            var message = string.Create(
                CultureInfo.InvariantCulture,
                $"{Prefix}the index line would exceed {ArchiveIndexLine.MaxLength} characters ({longest.Length}): shorten --name or --description\n");
            await io.Error.WriteAsync(message).ConfigureAwait(false);
            return Usage;
        }

        var deny = SourceDenyList.Build(environment, context.Options, context.Paths);
        var check = ArchiveChecks.Check(request, context.Options, context.Paths, deny, context.Patterns, today);
        if (check.Refusal is { } refusal)
        {
            await io.Error.WriteAsync(Prefix + "refused: " + Render(refusal, check.Detail) + "\n").ConfigureAwait(false);
            return Refused;
        }

        await io.Error.WriteAsync(Prefix + "refused: not implemented\n").ConfigureAwait(false);
        return Refused;
    }

    private static string Render(ArchiveRefusal refusal, string? detail) =>
        detail is null ? ArchiveRefusalWire.ToWire(refusal) : $"{ArchiveRefusalWire.ToWire(refusal)} ({detail})";

    private static async Task<int> RemoveAsync(string[] args, VerbIo io)
    {
        var (reference, error) = ArchiveArguments.ParseRemove(args);
        if (reference is null)
        {
            await io.Error.WriteAsync(Prefix + error + ArchiveArguments.UsageSuffixRemove + "\n").ConfigureAwait(false);
            return Usage;
        }

        await io.Error.WriteAsync(Prefix + "refused: not implemented\n").ConfigureAwait(false);
        return Refused;
    }

    private static async Task<int> ListAsync(string[] args, VerbIo io)
    {
        var (request, error) = ArchiveArguments.ParseList(args);
        if (request is null)
        {
            await io.Error.WriteAsync(Prefix + error + ArchiveArguments.UsageSuffixList + "\n").ConfigureAwait(false);
            return Usage;
        }

        return Archived;
    }

    private static async Task<int> ConfigurationErrorAsync(VerbIo io, string message)
    {
        await io.Error.WriteAsync(Prefix + "configuration error: " + message + "\n").ConfigureAwait(false);
        return Configuration;
    }
}

/// <summary>What a passed configuration gives the sub-verbs: the principal's paths, zone, secret patterns and archive options.</summary>
internal sealed record ArchiveContext(MemoryPaths Paths, TimeZoneInfo TimeZone, SecretPatterns Patterns, ArchiveOptions Options);
