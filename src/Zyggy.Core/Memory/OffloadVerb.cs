using System.Globalization;

using Zyggy.Core.Git;
using Zyggy.Core.M365;
using Zyggy.Core.M365.Graph;
using Zyggy.Core.M365.Guard;
using Zyggy.Core.Processes;
using Zyggy.Core.Verbs;

namespace Zyggy.Core.Memory;

/// <summary>
/// <c>zyggy memory archive offload [--dry-run | --probe | --retry &lt;project&gt;/&lt;slug&gt;]</c> (spec 38 CLI): usage, the lock (held → exit 0),
/// configuration (memory, secret patterns, <c>archive.json</c>, the failure state), the kill switch (<c>offload.enabled</c> false → exit 0
/// for a run; <c>--probe</c> and <c>--dry-run</c> still work so the owner can check before enabling), <c>m365.json</c> and the key, then the
/// run. Runs under <c>ZYGGY_HOOKS=off</c> — it is the one unattended archive write (AC-4). Exit 0 moved / nothing to do / disabled / lock
/// held, 3 configuration, 4 usage, 5 an item failed or is parked, 6 run-level failure, 7 committed with the push deferred.
/// </summary>
internal sealed class OffloadVerb(
    IReadOnlyDictionary<string, string?> environment,
    TimeProvider clock,
    Func<string, TimeZoneInfo> findTimeZone,
    IProcessRunner processes,
    HttpMessageHandler? graphHandler,
    bool checkKeyOwnership)
{
    private const string Prefix = "offload: ";

    public async Task<int> RunAsync(IReadOnlyList<string> args, VerbIo io, CancellationToken cancellationToken)
    {
        var (request, error) = ArchiveArguments.ParseOffload(args);
        if (request is null)
        {
            await io.Error.WriteAsync(Prefix + error + ArchiveArguments.UsageSuffixOffload + "\n").ConfigureAwait(false);
            return ArchiveVerb.Usage;
        }

        var statePaths = new ArchiveStatePaths(environment);
        using var gate = OffloadLock.TryAcquire(statePaths.LockFile);
        if (gate is null)
        {
            await io.Error.WriteAsync(Prefix + "another run is in progress\n").ConfigureAwait(false);
            return OffloadService.ExitOk;
        }

        var memory = MemoryEnvironment.Resolve(environment, findTimeZone);
        if (memory.Error is not null)
        {
            return await ConfigurationErrorAsync(io, memory.Error).ConfigureAwait(false);
        }

        var location = SecretPatternsLocation.Resolve(environment);
        var patterns = location is null
            ? new SecretPatternsLoad(null, "ZYGGY_SECRET_PATTERNS is not set (and no ZYGGY_INSTANCE_DIR or CLAUDE_PROJECT_DIR to derive it from)")
            : SecretPatterns.Load(location);
        if (patterns.Patterns is null)
        {
            return await ConfigurationErrorAsync(io, patterns.Error!).ConfigureAwait(false);
        }

        var configuration = ArchiveConfiguration.Load(environment, path => File.Exists(path) ? File.ReadAllText(path) : null);
        if (configuration.Options is null)
        {
            return await ConfigurationErrorAsync(io, configuration.Error!).ConfigureAwait(false);
        }

        var load = OffloadState.Load(statePaths);
        if (load.State is not { } state)
        {
            return await ConfigurationErrorAsync(io, load.Error!).ConfigureAwait(false);
        }

        var options = configuration.Options.Offload;
        if (!options.Enabled && request.Mode is OffloadMode.Run or OffloadMode.Retry)
        {
            await io.Error.WriteAsync(Prefix + "disabled (archive.json offload.enabled)\n").ConfigureAwait(false);
            return OffloadService.ExitOk;
        }

        if (request.Retry is { } retry && !state.TryRetry(OffloadState.Key(retry.Project, retry.Slug)))
        {
            await io.Error.WriteAsync($"{Prefix}{retry.Project.Value}/{retry.Slug.Value} is not parked{ArchiveArguments.UsageSuffixOffload}\n").ConfigureAwait(false);
            return ArchiveVerb.Usage;
        }

        var context = new M365VerbContext(environment, clock, findTimeZone, processes, graphHandler, checkKeyOwnership);
        var (session, _, sessionError) = M365Session.Load(context, baseOnly: false);
        if (session is null)
        {
            return await ConfigurationErrorAsync(io, sessionError!.StartsWith("configuration error: ", StringComparison.Ordinal) ? sessionError["configuration error: ".Length..] : sessionError).ConfigureAwait(false);
        }

        using var graph = session.CreateGraph(context);
        var archive = new ArchiveContext(memory.Paths!, memory.TimeZone!, patterns.Patterns, configuration.Options);
        return request.Mode switch
        {
            OffloadMode.Probe => await ProbeAsync(graph.Reader, options, io, cancellationToken).ConfigureAwait(false),
            OffloadMode.DryRun => await DryRunAsync(graph.Reader, archive, state, io, cancellationToken).ConfigureAwait(false),
            _ => await RunServiceAsync(graph, archive, state, statePaths, io, cancellationToken).ConfigureAwait(false),
        };
    }

    // Reads only: the drive, the root folder and its kind, one listing of its children.
    private static async Task<int> ProbeAsync(GraphReader reader, OffloadOptions options, VerbIo io, CancellationToken cancellationToken)
    {
        var (drive, root, failure) = await ResolveAsync(reader, options, cancellationToken).ConfigureAwait(false);
        if (failure is not null)
        {
            await io.Error.WriteAsync(Prefix + failure + "\n").ConfigureAwait(false);
            return OffloadService.ExitRunFailed;
        }

        var children = await reader.GetAsync($"/drives/{drive}/items/{root!.Id}/children?$top=1&$select=id", _ => false, cancellationToken).ConfigureAwait(false);
        if (children.Failure is { } listFailure)
        {
            await io.Error.WriteAsync($"{Prefix}root folder listing: {listFailure.Message}\n").ConfigureAwait(false);
            return OffloadService.ExitRunFailed;
        }

        await io.Out.WriteAsync($"drive: {drive} root: {options.RootFolder} ({root.Id}) ok\n").ConfigureAwait(false);
        return OffloadService.ExitOk;
    }

    // Selection and the drive/root resolution only: no write, no git.
    private async Task<int> DryRunAsync(GraphReader reader, ArchiveContext archive, OffloadState state, VerbIo io, CancellationToken cancellationToken)
    {
        var options = archive.Options.Offload;
        var (_, _, failure) = await ResolveAsync(reader, options, cancellationToken).ConfigureAwait(false);
        if (failure is not null)
        {
            await io.Error.WriteAsync(Prefix + failure + "\n").ConfigureAwait(false);
            return OffloadService.ExitRunFailed;
        }

        var rows = Archive(archive).List(new ArchiveListRequest(null, false, false), state);
        var plan = OffloadSelection.Select(rows, archive.Paths, options, state);
        var drivePaths = new DriveArchivePath(options.RootFolder);
        foreach (var candidate in plan.Candidates)
        {
            var drivePath = drivePaths.Item(candidate.Project, candidate.Slug, candidate.Sidecar.ItemExtension);
            await io.Out.WriteAsync($"would offload: {archive.Paths.Relative(candidate.ItemPath)} -> {drivePath}\n").ConfigureAwait(false);
        }

        if (plan.Deferred.Count > 0)
        {
            await io.Out.WriteAsync(string.Create(CultureInfo.InvariantCulture, $"deferred by the per-run caps: {plan.Deferred.Count}\n")).ConfigureAwait(false);
        }

        return OffloadService.ExitOk;
    }

    private async Task<int> RunServiceAsync(GraphParts graph, ArchiveContext archive, OffloadState state, ArchiveStatePaths statePaths, VerbIo io, CancellationToken cancellationToken)
    {
        var git = new GitClient(processes, new GitClientOptions(), clock);
        var publisher = new MemoryPublisher(git);
        var service = new OffloadService(
            archive.Paths, archive.TimeZone, clock, archive.Patterns, archive.Options.Offload, state, Archive(archive), publisher, git,
            archive.Paths.RootDirectory, graph.Reader, graph.DriveWriter, new ActionLog(new M365Paths(environment).StateDirectory))
        {
            StatePaths = statePaths,
        };
        var run = await service.RunAsync(cancellationToken).ConfigureAwait(false);

        foreach (var item in run.Items)
        {
            var name = item.ItemPath["archive/".Length..];
            switch (item.Outcome)
            {
                case OffloadOutcome.Moved or OffloadOutcome.Adopted:
                    await io.Out.WriteAsync($"offloaded: {item.ItemPath} -> {item.DrivePath}\ncommit: {item.Sha} {(item.Pushed ? "pushed" : "push deferred")}\n").ConfigureAwait(false);
                    break;
                case OffloadOutcome.Failed or OffloadOutcome.Parked:
                    var reason = OffloadReasonWire.ToWire(item.Reason!.Value) + (item.Detail is null ? string.Empty : $" ({item.Detail})");
                    await io.Error.WriteAsync($"{Prefix}{name}: {reason}{(item.Outcome == OffloadOutcome.Parked ? " — parked" : string.Empty)}\n").ConfigureAwait(false);
                    break;
            }

            if (item.Note is not null)
            {
                await io.Error.WriteAsync($"{Prefix}note: {item.Note}\n").ConfigureAwait(false);
            }
        }

        if (run.RunMessage is not null)
        {
            await io.Error.WriteAsync(Prefix + run.RunMessage + "\n").ConfigureAwait(false);
        }

        if (run.ExitCode == OffloadService.ExitItemFailed)
        {
            await io.Error.WriteAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"{Prefix}{run.Moved} moved, {run.Failed} failed, {run.ParkedTotal} parked — runbook \"Offload upload failed\"\n")).ConfigureAwait(false);
        }

        return run.ExitCode;
    }

    private static async Task<(string? Drive, DriveItemInfo? Root, string? Failure)> ResolveAsync(GraphReader reader, OffloadOptions options, CancellationToken cancellationToken)
    {
        if (await reader.SignInAsync(cancellationToken).ConfigureAwait(false) is { } signIn)
        {
            return (null, null, "sign-in failed: " + signIn.Message);
        }

        var drive = await reader.OneDriveIdAsync(cancellationToken).ConfigureAwait(false);
        if (drive.Failure is { } driveFailure)
        {
            return (null, null, "drive: " + driveFailure.Message);
        }

        var root = await reader.ItemByPathAsync(drive.Value!, options.RootFolder, cancellationToken).ConfigureAwait(false);
        return root.Failure is { } rootFailure ? (null, null, "root folder: " + rootFailure.Message)
            : root.Value is not { IsFolder: true } folder ? (null, null, $"root folder {options.RootFolder} missing or not a folder — runbook \"Offload root folder\"")
            : (drive.Value, folder, null);
    }

    private ArchiveService Archive(ArchiveContext context)
    {
        var publisher = new MemoryPublisher(new GitClient(processes, new GitClientOptions(), clock));
        return new ArchiveService(
            context.Paths, context.TimeZone, clock, context.Patterns, context.Options, SourceDenyList.Build(environment, context.Options, context.Paths), publisher, context.Paths.RootDirectory);
    }

    private static async Task<int> ConfigurationErrorAsync(VerbIo io, string message)
    {
        await io.Error.WriteAsync(Prefix + "configuration error: " + message + "\n").ConfigureAwait(false);
        return ArchiveVerb.Configuration;
    }
}
