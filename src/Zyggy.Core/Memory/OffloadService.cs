using Zyggy.Core.Git;
using Zyggy.Core.M365.Graph;
using Zyggy.Core.M365.Guard;

namespace Zyggy.Core.Memory;

/// <summary>What happened to one candidate.</summary>
internal enum OffloadOutcome
{
    /// <summary>Uploaded, verified, committed.</summary>
    Moved,

    /// <summary>An identical file was already on the drive: verified and committed without an upload.</summary>
    Adopted,

    /// <summary>A transient failure: counted, retried next run.</summary>
    Failed,

    /// <summary>A permanent failure, or the transient limit reached: never retried automatically.</summary>
    Parked,

    /// <summary>The run stopped at this item for a run-level reason; the item is untouched in git.</summary>
    Stopped,
}

/// <summary>One candidate's result: its item path, OneDrive path, outcome, reason and detail, the commit and whether it was pushed, a note.</summary>
internal sealed record OffloadItemResult(
    string Key, string ItemPath, string DrivePath, OffloadOutcome Outcome, OffloadReason? Reason, string? Detail, string? Sha, bool Pushed, string? Note);

/// <summary>A run: every candidate's result, the run-level reason and message when the run stopped, and the exit code.</summary>
internal sealed record OffloadRun(IReadOnlyList<OffloadItemResult> Items, OffloadReason? RunReason, string? RunMessage, int ExitCode, int ParkedTotal)
{
    public int Moved => Items.Count(i => i.Outcome is OffloadOutcome.Moved or OffloadOutcome.Adopted);

    public int Failed => Items.Count(i => i.Outcome == OffloadOutcome.Failed);
}

/// <summary>
/// One archive offload run (spec 38 Behaviors "Order is safety"): git preflight, sign-in, the drive and the root folder resolved, then per
/// candidate — bytes read and SHA-256-checked, project folder, existing target adopted or a new file uploaded, the copy re-read and compared
/// (parent, name, size, QuickXorHash), and only then the sidecar's offload part, one <c>archive offload</c> commit removing the item,
/// pushed, an <c>[observed]</c> inbox line and the state cleared. Every OneDrive write leaves one action-log row before the next.
/// No model is involved; nothing is ever deleted or overwritten on OneDrive; failures are closed reasons, never exceptions to the caller.
/// </summary>
internal sealed class OffloadService(
    MemoryPaths paths,
    TimeZoneInfo zone,
    TimeProvider clock,
    SecretPatterns secrets,
    OffloadOptions options,
    OffloadState state,
    ArchiveService archive,
    MemoryPublisher publisher,
    GitClient git,
    string repository,
    IGraphReader reader,
    Func<DriveWriteScope, IGraphDriveWriter> writerFactory,
    ActionLog log)
{
    public const int ExitOk = 0;
    public const int ExitItemFailed = 5;
    public const int ExitRunFailed = 6;
    public const int ExitPushDeferred = 7;

    public async Task<OffloadRun> RunAsync(CancellationToken cancellationToken)
    {
        var runStart = clock.GetUtcNow();
        var run = new RunContext(runStart);

        // 1. Git must be able to commit before anything is uploaded; a still-unpushed commit stops the run.
        var preflight = await publisher.PreflightAsync(repository, cancellationToken).ConfigureAwait(false);
        if (preflight.Detail is not null || preflight.PushPending)
        {
            return Finish(run, OffloadReason.GitError, "git error: " + (preflight.Detail ?? "push_pending"));
        }

        // 2. Sign in, the owner's OneDrive, the owner-created root folder (never created by the binary).
        if (await reader.SignInAsync(cancellationToken).ConfigureAwait(false) is { } signIn)
        {
            return Finish(run, OffloadReason.AuthFailed, "sign-in failed: " + signIn.Message);
        }

        var drive = await reader.OneDriveIdAsync(cancellationToken).ConfigureAwait(false);
        if (drive.Failure is { } driveFailure)
        {
            return Finish(run, ReadReason(driveFailure), "drive: " + driveFailure.Message);
        }

        var drivePaths = new DriveArchivePath(options.RootFolder);
        var root = await reader.ItemByPathAsync(drive.Value!, options.RootFolder, cancellationToken).ConfigureAwait(false);
        if (root.Failure is { } rootFailure)
        {
            return Finish(run, ReadReason(rootFailure), "root folder: " + rootFailure.Message);
        }

        if (root.Value is not { } rootItem || !rootItem.IsFolder)
        {
            return Finish(
                run,
                root.Value is null ? OffloadReason.RootMissing : OffloadReason.RootNotFolder,
                $"root folder {options.RootFolder} missing or not a folder — runbook \"Offload root folder\"");
        }

        var scope = new DriveWriteScope(drive.Value!, rootItem.Id, SimpleUploadLimit);
        var target = new DriveTarget(drive.Value!, rootItem.Id, drivePaths, scope, writerFactory(scope), preflight.Branch!);

        // 3. The candidates.
        var plan = OffloadSelection.Select(archive.List(new ArchiveListRequest(null, false, false), state), paths, options, state);
        foreach (var candidate in plan.Candidates)
        {
            var result = await OffloadAsync(candidate, target, run, cancellationToken).ConfigureAwait(false);
            run.Items.Add(result);
            if (run.StopReason is not null || (result.Outcome is OffloadOutcome.Moved or OffloadOutcome.Adopted && !result.Pushed))
            {
                break;
            }
        }

        return Finish(run, run.StopReason, run.StopMessage);
    }

    private async Task<OffloadItemResult> OffloadAsync(OffloadCandidate candidate, DriveTarget target, RunContext run, CancellationToken cancellationToken)
    {
        var key = OffloadState.Key(candidate.Project, candidate.Slug);
        var extension = candidate.Sidecar.ItemExtension;
        var itemRelative = paths.Relative(candidate.ItemPath);
        var fileName = DriveArchivePath.FileName(candidate.Slug, extension);
        var drivePath = target.Paths.Item(candidate.Project, candidate.Slug, extension);
        var item = new ItemContext(candidate, key, itemRelative, drivePath, fileName, target.Branch);

        // 1. The bytes, read once, against the sidecar's SHA-256.
        var (bytes, mismatch) = OffloadSelection.ReadAndCheck(candidate);
        if (bytes is null)
        {
            return Park(item, mismatch!.Value, null);
        }

        var quickXor = QuickXorHash.Base64(bytes);

        // 2. The project folder directly under the root.
        var folder = await ProjectFolderAsync(candidate.Project, target, run, cancellationToken).ConfigureAwait(false);
        if (folder.Id is null)
        {
            return Fail(item, folder.Failure!, run);
        }

        // An interrupted offload (AC-17): re-verify the copy the sidecar names, then commit; a mismatch restores the sidecar and parks.
        if (candidate.Resume)
        {
            var copy = await reader.ItemByIdAsync(target.DriveId, candidate.Sidecar.Offload!.DriveItemId, cancellationToken).ConfigureAwait(false);
            if (copy.Failure is { } readFailure)
            {
                return Fail(item, Classified.FromRead(readFailure), run);
            }

            if (!Matches(copy.Value, target.DriveId, folder.Id, fileName, bytes.LongLength, quickXor))
            {
                await git.CheckoutPathsAsync(repository, RepoPaths(paths.Relative(candidate.SidecarPath)), cancellationToken).ConfigureAwait(false);
                return Park(item, OffloadReason.VerifyFailed, "resume");
            }

            return await CommitAsync(item, candidate.Sidecar, OffloadOutcome.Moved, cancellationToken).ConfigureAwait(false);
        }

        // 3. An existing file of that name: adopted when identical, else a conflict (never overwritten).
        var existing = await reader.ChildAsync(target.DriveId, folder.Id, fileName, cancellationToken).ConfigureAwait(false);
        if (existing.Failure is { } existingFailure)
        {
            return Fail(item, Classified.FromRead(existingFailure), run);
        }

        DriveItemInfo uploaded;
        var outcome = OffloadOutcome.Moved;
        if (existing.Value is { } present)
        {
            if (!Identical(present, bytes.LongLength, quickXor))
            {
                return Park(item, OffloadReason.TargetConflict, null);
            }

            uploaded = present;
            outcome = OffloadOutcome.Adopted;
        }
        else
        {
            // 4. Upload a new file; one action-log row before anything else happens.
            var write = await target.Writer.CreateFileAsync(target.DriveId, folder.Id, fileName, bytes, cancellationToken).ConfigureAwait(false);
            if (!LogWrite(run, write, "archive-offload-upload", target.DriveId, folder.Id, fileName, bytes.LongLength))
            {
                return Stop(item);
            }

            if (write.Value is { } created)
            {
                uploaded = created;
            }
            else if (write.Failure == GraphWriteFailure.Conflict)
            {
                var again = await reader.ChildAsync(target.DriveId, folder.Id, fileName, cancellationToken).ConfigureAwait(false);
                if (again.Failure is { } againFailure)
                {
                    return Fail(item, Classified.FromRead(againFailure), run);
                }

                if (again.Value is not { } raced || !Identical(raced, bytes.LongLength, quickXor))
                {
                    return Park(item, OffloadReason.TargetConflict, write.Detail);
                }

                uploaded = raced;
                outcome = OffloadOutcome.Adopted;
            }
            else
            {
                return Fail(item, Classified.FromWrite(write), run);
            }
        }

        // 5. Verify: the copy re-read by id must be the bytes, in the folder, under the name. A failure leaves the copy and git untouched.
        var verify = await reader.ItemByIdAsync(target.DriveId, uploaded.Id, cancellationToken).ConfigureAwait(false);
        if (verify.Failure is { } verifyFailure)
        {
            return Fail(item, Classified.FromRead(verifyFailure), run);
        }

        if (!Matches(verify.Value, target.DriveId, folder.Id, fileName, bytes.LongLength, quickXor))
        {
            return Park(item, OffloadReason.VerifyFailed, null);
        }

        // 6. The sidecar's offload part (a webUrl that looks like a contact detail is not stored).
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).DateTime);
        var url = verify.Value!.WebUrl;
        string? note = null;
        if (url is not null && ContactDetailPatterns.Contains(url))
        {
            url = null;
            note = $"the OneDrive link of {itemRelative} looks like a contact detail; stored without drive_url";
        }

        var sidecar = candidate.Sidecar with
        {
            Updated = today,
            Offload = new ArchiveOffload(today, uploaded.Id, drivePath, url, quickXor),
        };
        MemoryFileWriter.Write(candidate.SidecarPath, sidecar.ToMemoryFile());
        var committed = await CommitAsync(item, sidecar, outcome, cancellationToken).ConfigureAwait(false);
        return committed with { Note = note ?? committed.Note };
    }

    // 7–9: git rm the item, one commit of item and sidecar, pushed; then the inbox line and the state. A failed commit restores the item
    // from HEAD (the sidecar keeps the offload part, so the next run resumes; plan assumption A4).
    private async Task<OffloadItemResult> CommitAsync(ItemContext item, ArchiveSidecar sidecar, OffloadOutcome outcome, CancellationToken cancellationToken)
    {
        var itemRepo = RepoPaths(item.ItemRelative);
        var sidecarRepo = RepoPaths(paths.Relative(item.Candidate.SidecarPath));
        var message = OffloadCommitMessage.For(
            item.Candidate.Project, item.Candidate.Slug, sidecar.ItemExtension, sidecar.Offload!.DriveItemId, sidecar.Sha256, sidecar.Offload.DriveQuickXor, item.Candidate.Size);

        var rm = await publisher.RemoveAsync(repository, itemRepo, cancellationToken).ConfigureAwait(false);
        var publish = rm is null
            ? await publisher.CommitAndPushAsync(repository, item.Branch, message, sidecarRepo, [.. itemRepo, .. sidecarRepo], cancellationToken).ConfigureAwait(false)
            : null;
        if (publish is not { Committed: true })
        {
            await git.CheckoutPathsAsync(repository, itemRepo, cancellationToken).ConfigureAwait(false);
            var detail = rm ?? publish!.GitStep;
            return Fail(item, new Classified(OffloadReason.GitError, detail, RunLevel: false));
        }

        var remember = new RememberService(paths, zone, clock, secrets).Remember(new RememberRequest(
            "observed", $" (project:{item.Candidate.Project.Value}) (source: archive offload)", string.Empty, $"Moved {item.ItemRelative} to OneDrive {item.DrivePath}"));
        state.Clear(item.Key);
        var note = remember.RefusedBy is null ? null : $"the inbox line for {item.ItemRelative} was refused ({remember.RefusedBy})";
        return new OffloadItemResult(item.Key, item.ItemRelative, item.DrivePath, outcome, publish.Pushed ? null : OffloadReason.PushDeferred, null, publish.Sha, publish.Pushed, note);
    }

    private async Task<(string? Id, Classified? Failure)> ProjectFolderAsync(Slug project, DriveTarget target, RunContext run, CancellationToken cancellationToken)
    {
        if (target.Folders.TryGetValue(project.Value, out var known))
        {
            return (known, null);
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var child = await reader.ChildAsync(target.DriveId, target.RootId, project.Value, cancellationToken).ConfigureAwait(false);
            if (child.Failure is { } failure)
            {
                return (null, Classified.FromRead(failure));
            }

            if (child.Value is { } folder)
            {
                if (!folder.IsFolder || folder.ParentId != target.RootId)
                {
                    return (null, new Classified(OffloadReason.TargetConflict, "project folder", RunLevel: false));
                }

                target.Scope.KnowProjectFolder(folder.Id);
                target.Folders[project.Value] = folder.Id;
                return (folder.Id, null);
            }

            if (attempt > 0)
            {
                break;
            }

            var write = await target.Writer.CreateFolderAsync(target.DriveId, target.RootId, project, cancellationToken).ConfigureAwait(false);
            if (!LogWrite(run, write, "archive-offload-folder", target.DriveId, target.RootId, project.Value, 0))
            {
                return (null, new Classified(OffloadReason.LogUnwritable, null, RunLevel: true));
            }

            if (write.Value is { } created)
            {
                target.Folders[project.Value] = created.Id;
                return (created.Id, null);
            }

            if (write.Failure != GraphWriteFailure.Conflict)
            {
                var classified = Classified.FromWrite(write);
                if (classified.RunLevel)
                {
                    run.Stop(classified.Reason, classified.Detail!);
                }

                return (null, classified);
            }

            // 409: someone created it meanwhile — read it again and use it if it is a folder under the root.
        }

        return (null, new Classified(OffloadReason.TargetConflict, "project folder", RunLevel: false));
    }

    // One row per write sent, before the next write; a row that cannot be appended stops the run.
    private bool LogWrite(RunContext run, GraphWrite<DriveItemInfo> write, string tool, string driveId, string parentId, string name, long size)
    {
        if (!write.Sent)
        {
            return true;
        }

        try
        {
            log.Append(ActionLog.BuildOffloadRow(clock.GetUtcNow(), run.Start, tool, driveId, parentId, name, size, write.Failure == GraphWriteFailure.None ? null : write.Detail ?? "unknown"));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            run.Stop(OffloadReason.LogUnwritable, "the action log cannot be appended — runbook \"Offload upload failed\"");
            return false;
        }
    }

    private OffloadItemResult Fail(ItemContext item, Classified failure, RunContext? run = null)
    {
        if (failure.RunLevel)
        {
            run?.Stop(failure.Reason, failure.Detail ?? OffloadReasonWire.ToWire(failure.Reason));
            return Stop(item, failure.Reason, failure.Detail);
        }

        if (OffloadReasonWire.IsPermanent(failure.Reason))
        {
            return Park(item, failure.Reason, failure.Detail);
        }

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).DateTime);
        var counted = state.RecordTransient(item.Key, failure.Reason, options.MaxFailures, today);
        return new OffloadItemResult(item.Key, item.ItemRelative, item.DrivePath, counted.Parked ? OffloadOutcome.Parked : OffloadOutcome.Failed, failure.Reason, failure.Detail, null, false, null);
    }

    private OffloadItemResult Park(ItemContext item, OffloadReason reason, string? detail)
    {
        state.Park(item.Key, reason, DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).DateTime));
        return new OffloadItemResult(item.Key, item.ItemRelative, item.DrivePath, OffloadOutcome.Parked, reason, detail, null, false, null);
    }

    private static OffloadItemResult Stop(ItemContext item, OffloadReason? reason = null, string? detail = null) =>
        new(item.Key, item.ItemRelative, item.DrivePath, OffloadOutcome.Stopped, reason, detail, null, false, null);

    private OffloadRun Finish(RunContext run, OffloadReason? runReason, string? message)
    {
        var deferred = run.Items.Any(i => i.Outcome is OffloadOutcome.Moved or OffloadOutcome.Adopted && !i.Pushed);
        var itemFailed = run.Items.Any(i => i.Outcome is OffloadOutcome.Failed or OffloadOutcome.Parked) || state.Parked.Count > 0;
        var exit = runReason is not null ? ExitRunFailed : deferred ? ExitPushDeferred : itemFailed ? ExitItemFailed : ExitOk;
        // "The last run failed" means the run as a whole (spec 38 AC-23); item failures show through their parks.
        state.RecordRun(clock.GetUtcNow(), runReason is not null, runReason);
        try
        {
            state.Save(StatePaths);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new OffloadRun(run.Items, runReason ?? OffloadReason.LogUnwritable, (message is null ? string.Empty : message + "; ") + "offload-state.json cannot be written", ExitRunFailed, state.Parked.Count);
        }

        return new OffloadRun(run.Items, runReason, message, exit, state.Parked.Count);
    }

    /// <summary>Gets where the failure state is saved.</summary>
    public required ArchiveStatePaths StatePaths { get; init; }

    /// <summary>Gets the largest item one request may upload (Graph's simple-upload limit; tests lower it).</summary>
    public long SimpleUploadLimit { get; init; } = DriveWriteScope.SimpleUploadLimit;

    private IReadOnlyList<string> RepoPaths(string relative) => ArchiveCommitMessage.RepoPaths(paths.Principal, relative);

    private static bool Identical(DriveItemInfo item, long size, string quickXor) =>
        !item.IsFolder && item.Size == size && string.Equals(item.QuickXorHash, quickXor, StringComparison.Ordinal);

    private static bool Matches(DriveItemInfo? item, string driveId, string folderId, string name, long size, string quickXor) =>
        item is not null && Identical(item, size, quickXor)
        && (item.DriveId is null || string.Equals(item.DriveId, driveId, StringComparison.Ordinal))
        && string.Equals(item.ParentId, folderId, StringComparison.Ordinal)
        && string.Equals(item.Name, name, StringComparison.Ordinal);

    // A failed read before the candidates: the run stops.
    private static OffloadReason ReadReason(GraphFailure failure) => Classified.FromRead(failure).Reason;

    private sealed record ItemContext(OffloadCandidate Candidate, string Key, string ItemRelative, string DrivePath, string FileName, string Branch);

    private sealed record DriveTarget(string DriveId, string RootId, DriveArchivePath Paths, DriveWriteScope Scope, IGraphDriveWriter Writer, string Branch)
    {
        public Dictionary<string, string> Folders { get; } = new(StringComparer.Ordinal);
    }

    private sealed class RunContext(DateTimeOffset start)
    {
        public DateTimeOffset Start { get; } = start;

        public List<OffloadItemResult> Items { get; } = [];

        public OffloadReason? StopReason { get; private set; }

        public string? StopMessage { get; private set; }

        public void Stop(OffloadReason reason, string message)
        {
            StopReason ??= reason;
            StopMessage ??= message;
        }
    }

    /// <summary>A failure as an offload reason; <see cref="RunLevel"/> when it stops the whole run (403: the grant is missing).</summary>
    private sealed record Classified(OffloadReason Reason, string? Detail, bool RunLevel)
    {
        public static Classified FromWrite(GraphWrite<DriveItemInfo> write) => write.Failure switch
        {
            GraphWriteFailure.Refused when write.Detail == "too_large" => new(OffloadReason.TooLarge, null, false),
            GraphWriteFailure.Refused => new(OffloadReason.WriteRefused, write.Detail, false),
            GraphWriteFailure.Conflict => new(OffloadReason.TargetConflict, write.Detail, false),
            GraphWriteFailure.TypeBlocked => new(OffloadReason.TypeBlocked, write.Detail, false),
            GraphWriteFailure.ClientError => new(OffloadReason.WriteRefused, write.Detail, false),
            GraphWriteFailure.Forbidden => new(OffloadReason.Forbidden, "forbidden — runbook \"Grant OneDrive write for the archive folder\"", true),
            GraphWriteFailure.Throttled => new(OffloadReason.Throttled, write.Detail, false),
            GraphWriteFailure.Transport => new(OffloadReason.TransportError, write.Detail, false),
            GraphWriteFailure.Auth => new(OffloadReason.AuthFailed, write.Detail, false),
            _ => new(OffloadReason.ServerError, write.Detail, false),
        };

        // GraphReader's exit-6 messages: forbidden (a read the grant does not allow), throttled, a transport failure, an auth failure, the rest.
        public static Classified FromRead(GraphFailure failure) =>
            failure.Message.StartsWith("forbidden", StringComparison.Ordinal) ? new(OffloadReason.Forbidden, "forbidden — runbook \"Grant OneDrive write for the archive folder\"", true)
            : failure.Message.StartsWith("throttled", StringComparison.Ordinal) ? new(OffloadReason.Throttled, null, false)
            : failure.Message.StartsWith("request failed", StringComparison.Ordinal) ? new(OffloadReason.TransportError, null, false)
            : failure.Message.StartsWith("unauthorized", StringComparison.Ordinal) || failure.Message.StartsWith("auth", StringComparison.Ordinal) || failure.ExitCode == 3
                ? new(OffloadReason.AuthFailed, null, false)
            : new(OffloadReason.ServerError, null, false);
    }
}
