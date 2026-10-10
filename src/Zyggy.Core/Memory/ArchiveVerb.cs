using System.Globalization;

using Zyggy.Core.Git;
using Zyggy.Core.Processes;
using Zyggy.Core.Verbs;

namespace Zyggy.Core.Memory;

/// <summary>
/// <c>zyggy memory archive add | list | remove</c> (spec 37): keeps a file the owner hands over in a session as a project archive item
/// with a sidecar and one <c>[stated]</c> index line. Exit 0 archived, 2 refused (<see cref="ArchiveRefusal"/>), 3 configuration error,
/// 4 usage, 6 git error, 7 committed but push deferred. <c>ZYGGY_HOOKS=off</c> refuses <c>add</c> and <c>remove</c> before anything is read.
/// Git runs through <see cref="IProcessRunner"/>; never <c>claude</c>. Builds no host.
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

    private readonly IProcessRunner _processes = new ProcessRunner(clock);

    // Tests: IANA ids need ICU off Linux, and the build is invariant-globalization; git is a recording fake.
    internal ArchiveVerb(IReadOnlyDictionary<string, string?> environment, TimeProvider clock, Func<string, TimeZoneInfo> findTimeZone, IProcessRunner? processes = null)
        : this(environment, clock)
    {
        _findTimeZone = findTimeZone;
        _processes = processes ?? _processes;
    }

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
            "add" => await AddAsync(rest, io, new ArchiveContext(memory.Paths!, memory.TimeZone!, load.Patterns, configuration.Options), cancellationToken).ConfigureAwait(false),
            "remove" => await RemoveAsync(rest, io, new ArchiveContext(memory.Paths!, memory.TimeZone!, load.Patterns, configuration.Options), cancellationToken).ConfigureAwait(false),
            _ => await ListAsync(rest, io, new ArchiveContext(memory.Paths!, memory.TimeZone!, load.Patterns, configuration.Options)).ConfigureAwait(false),
        };
    }

    private async Task<int> AddAsync(string[] args, VerbIo io, ArchiveContext context, CancellationToken cancellationToken)
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

        var outcome = await Service(context).AddAsync(request, cancellationToken).ConfigureAwait(false);
        if (await FailedAsync(outcome, io).ConfigureAwait(false) is { } failed)
        {
            return failed;
        }

        if (outcome.Note is not null)
        {
            await io.Error.WriteAsync(Prefix + "note: " + outcome.Note + "\n").ConfigureAwait(false);
        }

        return await CommitLinesAsync($"archived: {outcome.Item}\nsidecar: {outcome.Sidecar}\n", outcome, io).ConfigureAwait(false);
    }

    private ArchiveService Service(ArchiveContext context)
    {
        var deny = SourceDenyList.Build(environment, context.Options, context.Paths);
        var publisher = new MemoryPublisher(new GitClient(_processes, new GitClientOptions(), clock));
        return new ArchiveService(context.Paths, context.TimeZone, clock, context.Patterns, context.Options, deny, publisher, context.Paths.RootDirectory);
    }

    // A refusal (exit 2) or a git error (exit 6) as one stderr line; null when the outcome wrote and committed.
    private static async Task<int?> FailedAsync(ArchiveOutcome outcome, VerbIo io)
    {
        switch (outcome.Kind)
        {
            case ArchiveOutcomeKind.Refused:
                await io.Error.WriteAsync(Prefix + "refused: " + Render(outcome.Refusal!.Value, outcome.Detail) + "\n").ConfigureAwait(false);
                return Refused;
            case ArchiveOutcomeKind.GitError:
                await io.Error.WriteAsync(Prefix + "git error: " + outcome.Detail + "\n").ConfigureAwait(false);
                return GitError;
            default:
                return null;
        }
    }

    // The path lines, the inbox line and the commit line on stdout; exit 7 with a stderr line when the push was deferred.
    private static async Task<int> CommitLinesAsync(string pathLines, ArchiveOutcome outcome, VerbIo io)
    {
        var push = outcome.Pushed ? "pushed" : "push deferred";
        await io.Out.WriteAsync($"{pathLines}{outcome.Line}\ncommit: {outcome.Sha} {push}\n").ConfigureAwait(false);
        if (!outcome.Pushed)
        {
            await io.Error.WriteAsync($"{Prefix}committed {outcome.Sha}, push deferred\n").ConfigureAwait(false);
            return PushDeferred;
        }

        return Archived;
    }

    // secret_pattern's detail is "<pattern> (line <n>|name|description)" (spec 37 AC-10); every other detail goes in parentheses.
    private static string Render(ArchiveRefusal refusal, string? detail) =>
        detail is null ? ArchiveRefusalWire.ToWire(refusal)
        : refusal is ArchiveRefusal.SecretPattern ? $"{ArchiveRefusalWire.ToWire(refusal)} {detail}"
        : $"{ArchiveRefusalWire.ToWire(refusal)} ({detail})";

    private async Task<int> RemoveAsync(string[] args, VerbIo io, ArchiveContext context, CancellationToken cancellationToken)
    {
        var (reference, error) = ArchiveArguments.ParseRemove(args);
        if (reference is null)
        {
            await io.Error.WriteAsync(Prefix + error + ArchiveArguments.UsageSuffixRemove + "\n").ConfigureAwait(false);
            return Usage;
        }

        var outcome = await Service(context).RemoveAsync(reference, cancellationToken).ConfigureAwait(false);
        if (await FailedAsync(outcome, io).ConfigureAwait(false) is { } failed)
        {
            return failed;
        }

        var removed = string.Concat(new[] { outcome.Item, outcome.Sidecar }.OfType<string>().Select(path => $"removed: {path}\n"));
        return await CommitLinesAsync(removed, outcome, io).ConfigureAwait(false);
    }

    private async Task<int> ListAsync(string[] args, VerbIo io, ArchiveContext context)
    {
        var (request, error) = ArchiveArguments.ParseList(args);
        if (request is null)
        {
            await io.Error.WriteAsync(Prefix + error + ArchiveArguments.UsageSuffixList + "\n").ConfigureAwait(false);
            return Usage;
        }

        var rows = Service(context).List(request);
        await io.Out.WriteAsync(request.Json ? ArchiveListFormat.Json(rows) : ArchiveListFormat.Text(rows)).ConfigureAwait(false);
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
