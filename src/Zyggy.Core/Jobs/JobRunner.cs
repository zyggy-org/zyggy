using System.Globalization;
using System.Text;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Git;
using Zyggy.Core.Models;
using Zyggy.Core.Policy;
using Zyggy.Core.Registry;
using Zyggy.Core.Runs;

namespace Zyggy.Core.Jobs;

/// <summary>
/// Runs one verified, claimed job (founding spec §6): pre-flight, project lock, the fenced prompt through <see cref="IModelRunner"/>,
/// and always a <see cref="JobReply"/> — every failure carries one of the nine §9 reason strings. Its <see cref="RunAsync"/> has the
/// signature of <see cref="JobHandler"/>; the job's body is data, never instructions, and never reaches a log line.
/// </summary>
public sealed partial class JobRunner
{
    // Spec 15: the hook's rules when neither the job nor the node names any (the code default of claude.defaultAllowedTools).
    private static readonly string[] CodeDefaultAllowedTools = ["Read", "Grep", "Glob"];

    // The rule names of a blocked-calls line (JobHookVerb), in the order the report looks for them.
    private static readonly string[] BlockedRules = ["not in allowed_tools", "write outside the project", "network program", "hook error"];

    private readonly IModelRunner _model;
    private readonly IPolicySource _policy;
    private readonly GitClient _git;
    private readonly RegistrySnapshot _registry;
    private readonly JobRunnerOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<JobRunner> _log;
    private readonly JobPrompt _prompt = new();

    internal JobRunner(
        IModelRunner model,
        IPolicySource policy,
        GitClient git,
        RegistrySnapshot registry,
        IOptions<JobRunnerOptions> options,
        TimeProvider clock,
        ILogger<JobRunner> log)
    {
        ArgumentNullException.ThrowIfNull(options);
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _git = git ?? throw new ArgumentNullException(nameof(git));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _options = options.Value;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>The §6 pipeline for one verified, claimed job.</summary>
    /// <param name="job">The verified job envelope (the listener verified and claimed it, founding spec §5).</param>
    /// <param name="ct">Cancellation token; on cancellation the model process tree is killed and the exception propagates.</param>
    /// <returns>The reply the listener turns into a signed report; never an exception after argument validation.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="job"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="job"/> is not a <c>job</c> envelope (a caller bug).</exception>
    /// <exception cref="OperationCanceledException">Thrown only when <paramref name="ct"/> is cancelled.</exception>
    public Task<JobReply> RunAsync(Envelope.Envelope job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Type != EnvelopeType.Job || job.Job is null)
        {
            throw new ArgumentException("The job runner accepts only job envelopes.", nameof(job));
        }

        return RunCoreAsync(job, job.Job, ct);
    }

    private async Task<JobReply> RunCoreAsync(Envelope.Envelope job, JobFields fields, CancellationToken ct)
    {
        var id = job.Header.Id.Value;
        using var scope = _log.BeginScope(new Dictionary<string, object>
        {
            ["tenant"] = job.Header.Tenant.Value,
            ["machine"] = job.Header.To.Value,
            ["phase"] = "run",
            ["jobId"] = id,
        });
        var started = _clock.GetUtcNow();
        try
        {
            return await PipelineAsync(job, fields, started, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // §9: JobRunner never throws to the loop; every failure becomes a report.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // The type only: an exception message could carry the body.
            var type = ex.GetType().Name;
            LogRunnerException(id, type);
            return Reply(ReportStatus.Failed, RunFailureReasonWire.ToWire(RunFailureReason.ClaudeError), $"claude_error: runner_exception {type}\n", started);
        }
    }

    private async Task<JobReply> PipelineAsync(Envelope.Envelope job, JobFields fields, DateTimeOffset started, CancellationToken ct)
    {
        var id = job.Header.Id.Value;
        // Read first: pre-flight confines a path to the policy's devRoots (plan 14 A14).
        var policy = await _policy.GetAsync(ct).ConfigureAwait(false);
        var preFlight = await PreFlight.CheckAsync(fields, _git, _registry, policy.DevRoots, ct).ConfigureAwait(false);
        if (preFlight is PreFlightResult.Rejected rejected)
        {
            var reason = RunFailureReasonWire.ToWire(rejected.Reason);
            LogRejected(id, reason, rejected.Check);
            return Reply(ReportStatus.Rejected, reason, $"{reason}: {rejected.Check}\n", started);
        }

        // Spec 15: a hook that cannot start lets every tool call through (Claude Code treats it as a non-blocking error), so a job
        // whose hook binary is missing never starts — checked before the lock and the worktree, so nothing is left behind.
        var hookExecutable = _options.HookExecutable ?? TranscriptPaths.DefaultHookExecutable();
        if (!File.Exists(hookExecutable))
        {
            LogHookUnavailable(id, hookExecutable);
            return Reply(ReportStatus.Failed, RunFailureReasonWire.ToWire(RunFailureReason.ClaudeError), $"claude_error: hook unavailable: {hookExecutable}\n", started);
        }

        var project = ((PreFlightResult.Ok)preFlight).ProjectPath;
        using var projectLock = ProjectLock.TryAcquire(project, job.Header.Id, ProjectLock.CurrentProcessId, _clock.GetUtcNow());
        if (projectLock is null)
        {
            var lockPath = ProjectLock.PathOf(project);
            LogLocked(id, lockPath);
            return Reply(ReportStatus.Failed, RunFailureReasonWire.ToWire(RunFailureReason.Locked), $"locked: {lockPath}\n", started);
        }

        var nodeDir = _options.NodeDir ?? TranscriptPaths.DefaultNodeDir();
        SweepTranscripts(TranscriptPaths.TenantDirectory(nodeDir, job.Header.Tenant));

        // Spec 16: a worktree job runs in <nodeDir>/worktrees/<tenant>/<id> on zyggy/<id>; the lock stays on the project.
        var runDirectory = project;
        string? baseSha = null;
        var worktrees = new WorktreeRun(_git);
        if (fields.Worktree)
        {
            runDirectory = TranscriptPaths.WorktreeFor(nodeDir, job.Header.Tenant, job.Header.Id);
            var prepared = await worktrees.PrepareAsync(project, runDirectory, id, ct).ConfigureAwait(false);
            if (prepared.FetchFailed)
            {
                LogFetchFailed(id);
            }

            if (prepared is PrepareResult.Failed failed)
            {
                var gitError = RunFailureReasonWire.ToWire(RunFailureReason.GitError);
                LogWorktreeFailed(id, failed.Step);
                return Reply(ReportStatus.Failed, gitError, $"{gitError}: {failed.Step}\n", started);
            }

            baseSha = ((PrepareResult.Prepared)prepared).BaseSha;
        }

        var allowedTools = fields.AllowedTools is { Count: > 0 } tools ? tools : policy.DefaultAllowedTools;
        var minutes = fields.TimeoutMinutes ?? policy.DefaultTimeoutMinutes;

        // Spec 15: the rules travel as hook arguments, never through a file the model can reach; the blocked-calls file starts empty.
        var blockedFile = TranscriptPaths.BlockedFor(nodeDir, job.Header.Tenant, job.Header.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(blockedFile)!);
        File.Delete(blockedFile);
        List<string> hookArgs = ["hook", "pre-tool-use", "--root", runDirectory, "--blocked-file", blockedFile];
        foreach (var rule in allowedTools.Count > 0 ? allowedTools : CodeDefaultAllowedTools)
        {
            hookArgs.AddRange(["--allow", rule]);
        }

        // Spec 06 "ModelRunRequest for a job": no tool set, extra directory, schema, model or system prompt; the project's own
        // hooks, skills, MCP servers and memory apply (Isolation.None, ex-Q7).
        var request = new ModelRunRequest(_prompt.Render(job, runDirectory, allowedTools), runDirectory, _options.TimeoutOverride ?? TimeSpan.FromMinutes(minutes))
        {
            MaxTurns = policy.MaxTurns,
            AllowedTools = allowedTools,
            MaxBudgetUsd = policy.MaxBudgetUsd,
            Isolation = ModelSessionIsolation.None,
            PreToolUseHook = new ModelHookCommand(hookExecutable, hookArgs),
            // Founding spec §6 Invocation (amended by deliverable 12, D5): a job run is unattended — the memory and context writers do nothing.
            Environment = WithHooksOff(_options.ModelEnvironment),
            TranscriptPath = TranscriptPaths.For(nodeDir, job.Header.Tenant, job.Header.Id),
        };

        LogStarted(id, runDirectory, fields.Agent ?? "none", minutes);
        var result = await _model.RunAsync(request, ct).ConfigureAwait(false);
        var reply = Map(id, result, minutes, started);
        var hasResultLines = false;
        if (baseSha is not null)
        {
            var machine = job.Header.To.Value;
            var outcome = await worktrees.FinishAsync(project, runDirectory, id, baseSha, new GitIdentity("zyggy (" + machine + ")", machine + "@zyggy.org"), ct)
                .ConfigureAwait(false);
            reply = WithWorktree(id, reply, outcome);
            hasResultLines = outcome.ResultLines is not null;
        }

        reply = WithBlocked(id, reply, blockedFile, hasResultLines);
        LogFinished(id, reply, result);
        return reply;
    }

    // Spec 16 Behavior: files and diff_ref from the branch; a git failure turns a done run into failed/git_error, a failed or
    // timed-out run keeps its own status and reason.
    private JobReply WithWorktree(string id, JobReply reply, WorktreeOutcome outcome)
    {
        var body = outcome.ResultLines is { } lines
            ? Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(reply.Body.Span) + lines + "\n")
            : reply.Body.ToArray();
        var updated = reply with { Body = body, FilesChanged = outcome.FilesChanged, DiffRef = outcome.DiffRef, Finished = _clock.GetUtcNow() };
        if (outcome.RemoveFailed)
        {
            LogWorktreeNotRemoved(id);
        }

        if (outcome.FailedStep is { } step)
        {
            LogWorktreeFailed(id, step);
            if (reply.Status == ReportStatus.Done)
            {
                updated = updated with { Status = ReportStatus.Failed, Reason = RunFailureReasonWire.ToWire(RunFailureReason.GitError) };
            }
        }

        return updated;
    }

    // Spec 15 Behavior: one result line when the hook refused calls, after the worktree lines; the status stays the run's own.
    private JobReply WithBlocked(string id, JobReply reply, string blockedFile, bool hasResultLines)
    {
        string[] lines;
        try
        {
            lines = File.Exists(blockedFile) ? File.ReadAllLines(blockedFile).Where(l => l.Length > 0).ToArray() : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogBlockedUnreadable(id);
            return reply;
        }

        if (lines.Length == 0)
        {
            return reply;
        }

        var line = $"blocked: {lines.Length} tool call(s) outside this job's rules (first: {FirstRefusal(lines[0])})\n";
        var body = Encoding.UTF8.GetString(reply.Body.Span) + (hasResultLines ? string.Empty : "\n") + line;
        return reply with { Body = Encoding.UTF8.GetBytes(body) };
    }

    // A blocked-calls line is "<tool>: <rule> (<detail>)"; the report names the tool and the rule, never the detail (it may quote a path or command).
    private static string FirstRefusal(string line)
    {
        foreach (var rule in BlockedRules)
        {
            var marker = ": " + rule + " (";
            var at = line.IndexOf(marker, StringComparison.Ordinal);
            if (at >= 0)
            {
                return line[..at] + ": " + rule;
            }
        }

        return "unknown";
    }

    // The spec's Report mapping table: done with the summary; timeout with the partial text; claude_error for every other failure.
    private JobReply Map(string id, ModelRunResult result, int minutes, DateTimeOffset started)
    {
        var summary = ReportSummary.Extract(result);
        JobReply reply;
        if (result.Outcome == ModelRunOutcome.Succeeded)
        {
            reply = Reply(ReportStatus.Done, null, summary + "\n", started);
        }
        else if (result.Reason == RunFailureReason.Timeout)
        {
            reply = Reply(ReportStatus.Timeout, RunFailureReasonWire.ToWire(RunFailureReason.Timeout), $"timeout after {minutes} min\n\n{summary}\n", started);
        }
        else
        {
            if (result.Reason is { } other && other != RunFailureReason.ClaudeError)
            {
                LogUnexpectedReason(id, RunFailureReasonWire.ToWire(other));
            }

            reply = Reply(ReportStatus.Failed, RunFailureReasonWire.ToWire(RunFailureReason.ClaudeError), $"claude_error: {result.FailureDetail ?? "unknown"}\n\n{summary}\n", started);
        }

        return reply with
        {
            CostUsd = result.CostUsd,
            NumTurns = result.NumTurns,
            Model = result.Model,
            InputTokens = Clamp(result.InputTokens),
            OutputTokens = Clamp(result.OutputTokens),
        };
    }

    // §6: transcripts are kept for the retention; this tenant's directory only, once per run, on the injected clock.
    private void SweepTranscripts(string tenantDirectory)
    {
        var cutoff = (_clock.GetUtcNow() - _options.TranscriptRetention).UtcDateTime;
        if (!Directory.Exists(tenantDirectory))
        {
            return;
        }

        var removed = 0;
        // Spec 15: a run's blocked-calls file goes with its transcript.
        foreach (var file in Directory.EnumerateFiles(tenantDirectory, "*.jsonl").Concat(Directory.EnumerateFiles(tenantDirectory, "*.blocked")))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                    removed++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A transcript another process still holds is swept at a later run.
            }
        }

        if (removed > 0)
        {
            LogTranscriptsRemoved(removed, (int)_options.TranscriptRetention.TotalDays);
        }
    }

    /// <summary>The model environment with <c>ZYGGY_HOOKS=off</c> set last, so no configured value can switch the hooks back on (spec 12 A5).</summary>
    private static Dictionary<string, string> WithHooksOff(IReadOnlyDictionary<string, string> environment) =>
        new(environment, StringComparer.Ordinal) { ["ZYGGY_HOOKS"] = "off" };

    private static int? Clamp(long? tokens) => tokens is { } value ? (int)Math.Clamp(value, 0, int.MaxValue) : null;

    // Every runner reply starts with files_changed [] and no diff_ref; only a worktree run fills them (WithWorktree).
    private JobReply Reply(ReportStatus status, string? reason, string body, DateTimeOffset started) =>
        new(status, reason, Encoding.UTF8.GetBytes(body), started, _clock.GetUtcNow()) { FilesChanged = [], DiffRef = null };

    private void LogFinished(string id, JobReply reply, ModelRunResult result)
    {
        if (!_log.IsEnabled(LogLevel.Information))
        {
            return;
        }

        var status = EnvelopeWire.ToWire(reply.Status);
        var reason = reply.Reason is null ? string.Empty : "/" + reply.Reason;
        var seconds = (long)(reply.Finished - reply.Started).TotalSeconds;
        var cost = result.CostUsd?.ToString(CultureInfo.InvariantCulture) ?? "-";
        var turns = result.NumTurns?.ToString(CultureInfo.InvariantCulture) ?? "-";
        LogFinished(id, status, reason, seconds, cost, turns);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "job {Id}: started in {Project} (agent {Agent}, timeout {Minutes} min)")]
    private partial void LogStarted(string id, string project, string agent, int minutes);

    [LoggerMessage(Level = LogLevel.Information, Message = "job {Id}: finished {Status}{Reason} in {Seconds} s (cost {Cost} USD, turns {Turns})")]
    private partial void LogFinished(string id, string status, string reason, long seconds, string cost, string turns);

    [LoggerMessage(Level = LogLevel.Warning, Message = "job {Id}: rejected at pre-flight: {Reason} ({Check})")]
    private partial void LogRejected(string id, string reason, string check);

    [LoggerMessage(Level = LogLevel.Warning, Message = "job {Id}: project locked by {LockPath}")]
    private partial void LogLocked(string id, string lockPath);

    [LoggerMessage(Level = LogLevel.Warning, Message = "job {Id}: the model runner reported {Reason}; answered as claude_error")]
    private partial void LogUnexpectedReason(string id, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "transcripts: removed {Count} older than {Days} d")]
    private partial void LogTranscriptsRemoved(int count, int days);

    [LoggerMessage(Level = LogLevel.Error, Message = "job {Id}: runner exception {ExceptionType}")]
    private partial void LogRunnerException(string id, string exceptionType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "job {Id}: fetch from origin failed; running from the refs already present")]
    private partial void LogFetchFailed(string id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "job {Id}: worktree folder not removed; removed at the next run of this job or by hand")]
    private partial void LogWorktreeNotRemoved(string id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "job {Id}: hook unavailable: {Path}")]
    private partial void LogHookUnavailable(string id, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "job {Id}: blocked-calls file unreadable; the transcript shows the refusals")]
    private partial void LogBlockedUnreadable(string id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "job {Id}: worktree git step failed: {Step}")]
    private partial void LogWorktreeFailed(string id, string step);
}
