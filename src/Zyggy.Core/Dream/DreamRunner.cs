using System.Globalization;
using System.Text;

using Microsoft.Extensions.Logging;

using Zyggy.Core.Git;
using Zyggy.Core.Memory;
using Zyggy.Core.Models;
using Zyggy.Core.Runs;

namespace Zyggy.Core.Dream;

/// <summary>
/// One dream run (spec 28 Behaviors "One run, in order"): lock, snapshot and ledger, the batch loop with its caps, compressions, the
/// run-level breaker, the write, one commit and the push, and the run record. Never throws except
/// <see cref="OperationCanceledException"/>; every failure ends in the record.
/// </summary>
public sealed partial class DreamRunner
{
    private const string LedgerPath = ".dream/ledger.json";
    private const string QuarantinePath = ".dream/quarantine.md";

    private static readonly HashSet<string> NotBatchAttributable =
    [
        ModelFailureDetail.Auth, ModelFailureDetail.RateLimit, ModelFailureDetail.NotFound, ModelFailureDetail.StartFailed, ModelFailureDetail.Canceled,
    ];

    private readonly DreamEnvironment _environment;
    private readonly DreamOptions _options;
    private readonly DreamFiler _filer;
    private readonly Compressor _compressor;
    private readonly GitClient _git;
    private readonly TimeProvider _clock;
    private readonly ILogger<DreamRunner> _logger;

    internal DreamRunner(
        DreamEnvironment environment,
        DreamOptions options,
        DreamFiler filer,
        Compressor compressor,
        GitClient git,
        TimeProvider clock,
        ILogger<DreamRunner> logger)
    {
        _environment = environment;
        _options = options;
        _filer = filer;
        _compressor = compressor;
        _git = git;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Runs the dream once and appends its record to <c>dream-runs.jsonl</c>.</summary>
    /// <param name="trigger">What started the run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The run record (also when the run failed).</returns>
    /// <exception cref="OperationCanceledException">Thrown when the caller cancels.</exception>
    public async Task<DreamRunRecord> RunAsync(DreamTrigger trigger, CancellationToken cancellationToken)
    {
        var run = new RunState(Ulid.NewUlid().ToString(), _clock.GetUtcNow(), _clock.GetTimestamp(),
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), _environment.TimeZone).DateTime));
        var record = new DreamRunRecord
        {
            Run = run.Id,
            Trigger = DreamTriggerWire.ToWire(trigger),
            Version = _environment.Version,
            Started = run.Started,
            Outcome = DreamRunOutcomeWire.ToWire(DreamRunOutcome.Failed),
        };
        var triggerWire = record.Trigger;
        LogRunStarted(run.Id, triggerWire);

        try
        {
            using var held = DreamLock.TryAcquire(Path.Join(_environment.StateDirectory, "dream.lock"));
            if (held is null)
            {
                record = record with { Reason = RunFailureReasonWire.ToWire(RunFailureReason.Locked) };
                return Finish(record);
            }

            record = await RunLockedAsync(run, record, cancellationToken).ConfigureAwait(false);
            return Finish(record);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // The contract: every failure becomes the record, never an exception to the loop.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            var exceptionName = ex.GetType().Name;
            LogRunCrashed(run.Id, exceptionName);
            return Finish(record with
            {
                Outcome = DreamRunOutcomeWire.ToWire(DreamRunOutcome.Failed),
                Reason = RunFailureReasonWire.ToWire(run.Phase == Phase.Git ? RunFailureReason.GitError : RunFailureReason.ClaudeError),
                Detail = "unexpected_" + run.Phase.ToString().ToLowerInvariant(),
            });
        }
    }

    private async Task<DreamRunRecord> RunLockedAsync(RunState run, DreamRunRecord record, CancellationToken cancellationToken)
    {
        var paths = _environment.Paths;
        var repository = _environment.MemoryRoot;

        run.Phase = Phase.Git;
        var branch = await _git.CurrentBranchAsync(repository, cancellationToken).ConfigureAwait(false);
        if (branch is null || await _git.OperationInProgressAsync(repository, cancellationToken).ConfigureAwait(false))
        {
            return record with
            {
                Reason = RunFailureReasonWire.ToWire(RunFailureReason.GitError),
                Detail = branch is null ? "not_on_branch" : "operation_in_progress",
            };
        }

        // AC-20: a dream commit the last run could not push goes out before anything else.
        var unpushed = await _git.UnpushedCommitSubjectsAsync(repository, branch, cancellationToken).ConfigureAwait(false);
        if (unpushed.Any(s => s.StartsWith("dream ", StringComparison.Ordinal))
            && !await PushAsync(repository, branch, cancellationToken).ConfigureAwait(false))
        {
            return record with
            {
                Outcome = DreamRunOutcomeWire.ToWire(DreamRunOutcome.Committed),
                Commit = await _git.RevParseAsync(repository, "HEAD", cancellationToken).ConfigureAwait(false),
                Pushed = false,
                Detail = "push_pending",
            };
        }

        // AC-26: undo a run killed between its write and its commit, unless someone edited a path since.
        if (PendingRecovery.Plan(paths) is { } pending)
        {
            if (pending.Dirty is not null)
            {
                return record with { Outcome = DreamRunOutcomeWire.ToWire(DreamRunOutcome.Aborted), Check = DreamCheckWire.ToWire(DreamCheck.DirtyPending) };
            }

            var restore = await _git.CheckoutPathsAsync(repository, DreamCommitter.RepoPaths(_environment.Principal, pending.Restore), cancellationToken)
                .ConfigureAwait(false);
            if (!restore.Succeeded)
            {
                return record with { Reason = RunFailureReasonWire.ToWire(RunFailureReason.GitError), Detail = "recovery_failed" };
            }

            foreach (var created in pending.Delete)
            {
                File.Delete(Path.Join(paths.PrincipalDirectory, created));
            }

            DreamWriter.DeleteMarker(paths);
            LogRecovered(run.Id, pending.Run);
        }

        var carried = PassThrough.Carried(await _git.StatusEntriesAsync(repository, cancellationToken).ConfigureAwait(false), _environment.Principal, paths);

        run.Phase = Phase.Start;
        var secrets = SecretPatterns.Load(_environment.SecretPatternsPath);
        if (secrets.Patterns is null)
        {
            return record with { Detail = "secret_patterns_unreadable" };
        }

        var snapshot = MemorySnapshot.Load(paths);
        var ledgerLoad = DreamLedger.Load(snapshot.Files.GetValueOrDefault(LedgerPath)?.Text);
        if (ledgerLoad.Ledger is not { } ledger)
        {
            return record with
            {
                Reason = ledgerLoad.Status == DreamLedgerLoadStatus.Unsupported ? RunFailureReasonWire.ToWire(RunFailureReason.SchemaUnsupported) : null,
                Detail = ledgerLoad.Status == DreamLedgerLoadStatus.Unsupported ? "ledger_schema" : "ledger_invalid",
            };
        }

        var quarantine = QuarantinedHashes(snapshot.Files.GetValueOrDefault(QuarantinePath)?.Text);
        var batchStatePath = Path.Join(_environment.StateDirectory, "dream-batch.json");
        var batchState = BatchSizeState.Parse(File.Exists(batchStatePath) ? await File.ReadAllTextAsync(batchStatePath, cancellationToken).ConfigureAwait(false) : null, _options);

        var set = new WorkingSet(snapshot);
        var staged = ledger.Clone();
        var context = new DreamRunContext(paths, _environment.StateDirectory, run.LocalDate, _options)
        {
            Secrets = secrets.Patterns,
            CarriedPaths = carried.ReadOnly,
        };
        var batches = new List<DreamBatchRecord>();
        var quarantined = new List<DreamBatchLine>();
        DreamCheck? stopCheck = null;
        (RunFailureReason Reason, string Detail)? stopFailure = null;
        decimal spent = 0m;
        var newCategories = 0;

        run.Phase = Phase.Batches;
        while (batches.Count < _options.MaxBatchesPerRun
               && _clock.GetElapsedTime(run.Timestamp) < TimeSpan.FromMinutes(_options.RunMaxMinutes)
               && _options.RunMaxBudgetUsd - spent >= _options.CallMaxBudgetUsd)
        {
            var batch = BatchPlanner.Plan(snapshot, staged, quarantine, batchState.CurrentLines, _options.BatchMaxBytes);
            if (batch is null)
            {
                break;
            }

            var outcome = await _filer.FileBatchAsync(batch, snapshot, set, context with { NewCategoriesSoFar = newCategories }, cancellationToken)
                .ConfigureAwait(false);
            switch (outcome)
            {
                case BatchAccepted accepted:
                    foreach (var file in batch.Lines.GroupBy(l => l.RelativePath, StringComparer.Ordinal))
                    {
                        staged.Consume(file.Key, file.Select(l => l.Hash), run.LocalDate);
                    }

                    batchState = batchState.OnSucceeded(_options);
                    newCategories += accepted.Counts.CategoriesCreated;
                    spent += accepted.CostUsd ?? 0m;
                    batches.Add(BatchRecord(accepted.Counts, accepted.CostUsd, accepted.Turns, accepted.Duration, "accepted"));
                    LogBatch(run.Id, batches.Count, "accepted", batch.Lines.Count);
                    continue;

                case BatchAborted aborted:
                    spent += aborted.CostUsd ?? 0m;
                    var abortedResult = "aborted:" + DreamCheckWire.ToWire(aborted.Check);
                    batches.Add(BatchRecord(Empty(batch), aborted.CostUsd, aborted.Turns, aborted.Duration, abortedResult));
                    LogBatch(run.Id, batches.Count, abortedResult, batch.Lines.Count);
                    batchState = batchState.OnAttributableFailure(_options, batch.Lines[0].Hash);
                    if (Quarantine(ref batchState, batch, set, quarantine, quarantined))
                    {
                        continue;
                    }

                    stopCheck = aborted.Check;
                    break;

                case BatchFailed failed:
                    spent += failed.CostUsd ?? 0m;
                    var failedResult = $"failed:{RunFailureReasonWire.ToWire(failed.Reason)}:{failed.Detail}";
                    batches.Add(BatchRecord(Empty(batch), failed.CostUsd, failed.Turns, failed.Duration, failedResult));
                    LogBatch(run.Id, batches.Count, failedResult, batch.Lines.Count);
                    if (failed.Reason == RunFailureReason.Timeout || (failed.Reason == RunFailureReason.ClaudeError && !NotBatchAttributable.Contains(failed.Detail)))
                    {
                        batchState = batchState.OnAttributableFailure(_options, batch.Lines[0].Hash);
                        if (Quarantine(ref batchState, batch, set, quarantine, quarantined))
                        {
                            continue;
                        }
                    }

                    stopFailure = (failed.Reason, failed.Detail);
                    break;
            }

            break;
        }

        Directory.CreateDirectory(_environment.StateDirectory);
        await File.WriteAllTextAsync(batchStatePath, batchState.Serialize(), cancellationToken).ConfigureAwait(false);

        var compressions = new List<DreamCompressionRecord>();
        if (stopCheck is null && stopFailure is null && _options.MaxCompressionsPerRun > 0)
        {
            run.Phase = Phase.Compressions;
            foreach (var compression in await _compressor.CompressAsync(set, context, cancellationToken).ConfigureAwait(false))
            {
                spent += compression.CostUsd ?? 0m;
                compressions.Add(new DreamCompressionRecord(compression.Path,
                    compression.Accepted ? "accepted" : compression.Rejected is { } r ? "rejected:" + DreamCheckWire.ToWire(r)
                        : $"failed:{RunFailureReasonWire.ToWire(compression.Failure!.Value)}", compression.CostUsd));
            }
        }

        var acceptedCount = batches.Count(b => b.Result == "accepted");
        record = record with
        {
            Batches = batches,
            Compressions = compressions,
            Quarantined = quarantined.Count,
            CostUsdTotal = spent,
            InboxRemaining = Remaining(snapshot, staged, quarantine),
        };

        if (DreamChecks.CheckRun(snapshot, set, _options) is { } runCheck)
        {
            return record with { Outcome = DreamRunOutcomeWire.ToWire(DreamRunOutcome.Aborted), Check = DreamCheckWire.ToWire(runCheck) };
        }

        if (stopCheck is null && stopFailure is null)
        {
            var rollup = Rollup.Plan(snapshot, staged, run.LocalDate, _clock.GetUtcNow(), _options);
            Rollup.Apply(rollup, set, staged);
            record = record with { Rollup = new DreamRollupRecord(rollup.Archives.Sum(a => a.Days.Count), rollup.InboxDeletions.Count) };
            if (Rollup.CheckDeletions(set, rollup) is { } deletionCheck)
            {
                return record with { Outcome = DreamRunOutcomeWire.ToWire(DreamRunOutcome.Aborted), Check = DreamCheckWire.ToWire(deletionCheck) };
            }
        }

        var ledgerText = staged.Serialize();
        if (ledgerText != ledger.Serialize())
        {
            set.Write(LedgerPath, ledgerText);
        }

        var runOutcome = stopCheck is not null || stopFailure is not null
            ? (acceptedCount > 0 ? DreamRunOutcome.Partial : stopCheck is not null ? DreamRunOutcome.Aborted : DreamRunOutcome.Failed)
            : DreamRunOutcome.Committed;
        record = record with
        {
            Check = stopCheck is { } sc ? DreamCheckWire.ToWire(sc) : null,
            Reason = stopFailure is { } sf ? RunFailureReasonWire.ToWire(sf.Reason) : null,
            Detail = stopFailure?.Detail,
        };

        // A run that accepted nothing before its abort or failure commits nothing at all (exit 5 or 6, tree untouched).
        if (runOutcome is DreamRunOutcome.Aborted or DreamRunOutcome.Failed)
        {
            return record with { Outcome = DreamRunOutcomeWire.ToWire(runOutcome) };
        }

        // AC-25: auto/ and daily/ as found (a secret-bearing file withheld), and other writers' unstaged durable edits (carried).
        var changed = set.ChangedPaths.ToHashSet(StringComparer.Ordinal);
        var passThrough = PassThrough.Classify(await _git.StatusEntriesAsync(repository, cancellationToken).ConfigureAwait(false),
            _environment.Principal, snapshot, secrets.Patterns, changed);
        var carriedCommit = carried.CommitAsFound.Where(p => !changed.Contains(p)).ToList();
        record = record with { Withheld = passThrough.Withheld, Carried = carriedCommit };

        if (changed.Count == 0 && passThrough.Include.Count == 0 && carriedCommit.Count == 0)
        {
            var nothing = runOutcome == DreamRunOutcome.Committed ? DreamRunOutcome.NothingToDo : runOutcome;
            return record with { Outcome = DreamRunOutcomeWire.ToWire(nothing) };
        }

        // A durable file changed on disk after the snapshot is someone else's work: write nothing.
        if (changed.Where(p => !p.StartsWith(".dream/", StringComparison.Ordinal)).Any(p => ChangedOnDisk(snapshot, p)))
        {
            return record with { Outcome = DreamRunOutcomeWire.ToWire(DreamRunOutcome.Aborted), Check = DreamCheckWire.ToWire(DreamCheck.ConcurrentEdit) };
        }

        record = record with { Outcome = DreamRunOutcomeWire.ToWire(runOutcome) };
        run.Phase = Phase.Write;
        var written = new DreamWriter().Write(paths, set, run.Id);

        run.Phase = Phase.Git;
        var repoPaths = DreamCommitter.RepoPaths(_environment.Principal, written.Concat(passThrough.Include).Concat(carriedCommit));
        var added = DreamCommitter.RepoPaths(_environment.Principal, written.Where(p => !set.ExistedBefore(p) && set.Exists(p))
            .Concat(passThrough.Untracked).Concat(carried.Untracked.Where(carriedCommit.Contains)));
        var add = await _git.AddAsync(repository, added, cancellationToken).ConfigureAwait(false);
        var commit = add.Succeeded
            ? await _git.CommitOnlyAsync(repository, DreamCommitter.Message(record, run.LocalDate), repoPaths, cancellationToken).ConfigureAwait(false)
            : add;
        if (!commit.Succeeded)
        {
            LogGitFailed(run.Id, "commit");
            return record with
            {
                Outcome = DreamRunOutcomeWire.ToWire(DreamRunOutcome.Failed),
                Reason = RunFailureReasonWire.ToWire(RunFailureReason.GitError),
                Detail = add.Succeeded ? "commit_failed" : "add_failed",
            };
        }

        var sha = await _git.RevParseAsync(repository, "HEAD", cancellationToken).ConfigureAwait(false);
        DreamWriter.DeleteMarker(paths);
        LogCommitted(run.Id, sha ?? "?");
        var pushed = await PushAsync(repository, branch, cancellationToken).ConfigureAwait(false);
        return record with { Commit = sha, Pushed = pushed };
    }

    // AC-20: push; when rejected, fetch, rebase the run's commit once and push again; a conflict aborts the rebase and keeps the
    // commit for the next run. Any other push failure also defers the push (Assumption 7). Never a force push.
    private async Task<bool> PushAsync(string repository, string branch, CancellationToken cancellationToken)
    {
        var push = await _git.PushAsync(repository, branch, cancellationToken).ConfigureAwait(false);
        if (!push.Succeeded && IsRejected(push.Stderr))
        {
            var fetch = await _git.FetchAsync(repository, cancellationToken).ConfigureAwait(false);
            var rebase = fetch.Succeeded ? await _git.RebaseAsync(repository, branch, cancellationToken).ConfigureAwait(false) : fetch;
            if (rebase.Succeeded)
            {
                push = await _git.PushAsync(repository, branch, cancellationToken).ConfigureAwait(false);
            }
            else if (fetch.Succeeded)
            {
                await _git.RebaseAbortAsync(repository, cancellationToken).ConfigureAwait(false);
            }
        }

        LogPushed(push.Succeeded);
        return push.Succeeded;
    }

    private static bool ChangedOnDisk(MemorySnapshot snapshot, string relative)
    {
        var full = Path.Join(snapshot.Paths.PrincipalDirectory, relative);
        return snapshot.Files.TryGetValue(relative, out var file)
            ? !File.Exists(full) || MemorySnapshot.Hash(File.ReadAllBytes(full)) != file.Sha256
            : File.Exists(full);
    }

    private static bool IsRejected(string stderr) =>
        stderr.Contains("[rejected]", StringComparison.Ordinal) || stderr.Contains("non-fast-forward", StringComparison.Ordinal)
        || stderr.Contains("fetch first", StringComparison.Ordinal);

    // AC-12: after quarantineAfter failures at the minimum size with the same first line, the batch's lines leave the backlog.
    private bool Quarantine(ref BatchSizeState state, DreamBatch batch, WorkingSet set, HashSet<string> quarantine, List<DreamBatchLine> quarantined)
    {
        if (!state.ShouldQuarantine(_options))
        {
            return false;
        }

        var text = new StringBuilder(set.Text(QuarantinePath) ?? "# Lines the dream gave up on after repeated failures. Never offered again.\n");
        foreach (var line in batch.Lines)
        {
            quarantine.Add(line.Hash);
            quarantined.Add(line);
            text.Append(CultureInfo.InvariantCulture, $"- {line.Hash} {line.RelativePath}: {line.Text}\n");
        }

        set.Write(QuarantinePath, text.ToString());
        state = state with { ConsecutiveFailures = 0, FirstLineHash = null };
        return true;
    }

    private static HashSet<string> QuarantinedHashes(string? text)
    {
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in (text ?? string.Empty).Split('\n'))
        {
            var parts = line.Split(' ', 3);
            if (parts.Length >= 2 && parts[0] == "-" && parts[1].Length == 16)
            {
                hashes.Add(parts[1]);
            }
        }

        return hashes;
    }

    private static DreamInboxRemaining Remaining(MemorySnapshot snapshot, DreamLedger ledger, HashSet<string> quarantine)
    {
        var files = 0;
        var lines = 0;
        foreach (var file in snapshot.Files.Values.Where(f => BatchPlanner.Source(f.RelativePath) is not null))
        {
            var open = MemorySnapshot.BodyLines(file.Text).Select(l => LineHash.Of(l.Text)).Distinct(StringComparer.Ordinal)
                .Count(h => !ledger.IsConsumed(file.RelativePath, h) && !quarantine.Contains(h));
            if (open > 0)
            {
                files++;
                lines += open;
            }
        }

        return new DreamInboxRemaining(files, lines);
    }

    private static BatchCounts Empty(DreamBatch batch) => new(batch.Lines.Count, 0, 0, 0, new Dictionary<string, int>(), 0, 0, 0);

    private static DreamBatchRecord BatchRecord(BatchCounts counts, decimal? cost, int? turns, TimeSpan duration, string result) =>
        new(counts.Lines, counts.Filed, counts.Merged, counts.Duplicate, counts.Dropped, counts.FilesCreated, counts.FilesEdited,
            counts.CategoriesCreated, cost, turns, (long)duration.TotalMilliseconds, result);

    private DreamRunRecord Finish(DreamRunRecord record)
    {
        var finished = record with { Ended = _clock.GetUtcNow() };
        DreamRunRecordStore.Append(_environment.StateDirectory, finished);
        var why = finished.Check ?? finished.Reason ?? "-";
        LogRunEnded(finished.Run, finished.Outcome, why);
        return finished;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "dream {Run} started ({Trigger})")]
    private partial void LogRunStarted(string run, string trigger);

    [LoggerMessage(Level = LogLevel.Information, Message = "dream {Run} batch {Number}: {Result} ({Lines} lines)")]
    private partial void LogBatch(string run, int number, string result, int lines);

    [LoggerMessage(Level = LogLevel.Information, Message = "dream {Run} committed {Sha}")]
    private partial void LogCommitted(string run, string sha);

    [LoggerMessage(Level = LogLevel.Warning, Message = "dream {Run} recovered the pending files of dead run {DeadRun}")]
    private partial void LogRecovered(string run, string deadRun);

    [LoggerMessage(Level = LogLevel.Information, Message = "dream push: {Pushed}")]
    private partial void LogPushed(bool pushed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "dream {Run} git {Step} failed")]
    private partial void LogGitFailed(string run, string step);

    [LoggerMessage(Level = LogLevel.Error, Message = "dream {Run} failed unexpectedly: {Exception}")]
    private partial void LogRunCrashed(string run, string exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "dream {Run} ended: {Outcome} ({Why})")]
    private partial void LogRunEnded(string run, string outcome, string why);

    private enum Phase
    {
        Start,
        Batches,
        Compressions,
        Write,
        Git,
    }

    private sealed class RunState(string id, DateTimeOffset started, long timestamp, DateOnly localDate)
    {
        public string Id { get; } = id;

        public DateTimeOffset Started { get; } = started;

        public long Timestamp { get; } = timestamp;

        public DateOnly LocalDate { get; } = localDate;

        public Phase Phase { get; set; } = Phase.Start;
    }
}
