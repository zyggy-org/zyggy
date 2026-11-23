using System.Globalization;
using System.Text;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Git;
using Zyggy.Core.Models;
using Zyggy.Core.Policy;
using Zyggy.Core.Processes;
using Zyggy.Core.Registry;
using Zyggy.Core.Runs;

namespace Zyggy.Core.Jobs;

/// <summary>
/// Runs one verified, claimed job (founding spec §6): pre-flight, project lock, the fenced prompt through <see cref="IModelRunner"/>,
/// and always a <see cref="JobReply"/> — every failure carries one of the closed §9 reason strings. Its <see cref="RunAsync"/> has the
/// signature of <see cref="JobHandler"/>; the job's body is data, never instructions, and never reaches a log line.
/// </summary>
public sealed partial class JobRunner
{
    // Spec 15: the hook's rules when neither the job nor the node names any (the code default of claude.defaultAllowedTools).
    private static readonly string[] CodeDefaultAllowedTools = ["Read", "Grep", "Glob"];

    // The rule names of a blocked-calls line (JobHookVerb), in the order the report looks for them.
    private static readonly string[] BlockedRules = ["not in allowed_tools", "write outside the project", "network program", "read outside the read root", "hook error"];

    private static readonly TimeSpan HookSelfTestTimeout = TimeSpan.FromSeconds(15);

    private readonly IModelRunner _model;
    private readonly IPolicySource _policy;
    private readonly GitClient _git;
    private readonly IProcessRunner _processes;
    private readonly RegistrySnapshot _registry;
    private readonly JobRunnerOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<JobRunner> _log;
    private readonly JobPrompt _prompt = new();

    internal JobRunner(
        IModelRunner model,
        IPolicySource policy,
        GitClient git,
        IProcessRunner processes,
        RegistrySnapshot registry,
        IOptions<JobRunnerOptions> options,
        TimeProvider clock,
        ILogger<JobRunner> log)
    {
        ArgumentNullException.ThrowIfNull(options);
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _git = git ?? throw new ArgumentNullException(nameof(git));
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
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

        // Spec 59: a fetch job reads one file through git; no model, so no hook either.
        if (fields.Kind == JobKind.Fetch)
        {
            return await FetchAsync(job, fields, ((PreFlightResult.Ok)preFlight).ProjectPath, policy.FetchMaxBytes, started, ct).ConfigureAwait(false);
        }

        // Spec 59 slice 2: a command job runs one allow-listed argv of this node's own node.json; no model, so no hook either.
        if (fields.Kind == JobKind.Command)
        {
            return await CommandAsync(job, fields, ((PreFlightResult.Ok)preFlight).ProjectPath, policy, started, ct).ConfigureAwait(false);
        }

        // Spec 58 decision B as corrected by the owner (2026-11-23): Outlook on a work machine changes nothing for other work and refuses
        // no job for its rules. A mail job (its rules name zyggy outlook) runs there with its file reads and outlook read verbs only: every
        // other rule is dropped from its tools and its hook. Every other job runs as before; the hook refuses its zyggy outlook calls and
        // it gets no work mail marker.
        var allowedTools = fields.AllowedTools is { Count: > 0 } tools ? tools : policy.DefaultAllowedTools;
        IReadOnlyList<string> hookRules = allowedTools.Count > 0 ? allowedTools : CodeDefaultAllowedTools;
        if (policy.WorkMail && Outlook.WorkMailToolMix.IsMailJob(hookRules))
        {
            // None kept (e.g. only a send rule): the file reads, never an empty list that would fall back to wider tools.
            var mailRules = Outlook.WorkMailToolMix.MailRules(hookRules);
            if (mailRules.Count < hookRules.Count)
            {
                LogMailJobNarrowed(id, hookRules.Count - mailRules.Count);
            }

            IReadOnlyList<string> kept = mailRules.Count > 0 ? mailRules : CodeDefaultAllowedTools;

            hookRules = kept;
            allowedTools = kept;
        }

        var mailJob = Outlook.WorkMailToolMix.IsMailJob(hookRules);

        // Spec 15: a hook that cannot start lets every tool call through (Claude Code treats it as a non-blocking error), so a job
        // whose hook binary is missing, or does not refuse an empty call, never starts — checked before the lock and the worktree, so
        // nothing is left behind.
        var hookExecutable = _options.HookExecutable ?? TranscriptPaths.DefaultHookExecutable();
        if (!File.Exists(hookExecutable) || !await HookRefusesAnEmptyCallAsync(hookExecutable, ct).ConfigureAwait(false))
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
        string? defaultBranch = null;
        var worktrees = new WorktreeRun(_git);
        if (fields.Worktree)
        {
            runDirectory = TranscriptPaths.WorktreeFor(nodeDir, job.Header.Tenant, job.Header.Id);
            var prepared = await worktrees.PrepareAsync(project, runDirectory, id, fields.Base, NamedBranchOf(job, fields), fields.Continue?.Value, ct).ConfigureAwait(false);
            if (prepared.FetchFailed)
            {
                LogFetchFailed(id);
            }

            // Spec 53: a refusal before the model travels the path of pre-flight's rejection (report in reports/, claimed file removed).
            if (prepared is PrepareResult.Refused refused)
            {
                var branchRefused = RunFailureReasonWire.ToWire(RunFailureReason.BranchRefused);
                LogBranchRefused(id, refused.Rule);
                return Reply(ReportStatus.Rejected, branchRefused, $"{branchRefused}: {refused.Rule}\n", started);
            }

            if (prepared is PrepareResult.Failed failed)
            {
                var gitError = RunFailureReasonWire.ToWire(RunFailureReason.GitError);
                LogWorktreeFailed(id, failed.Step);
                var detail = failed.Detail is null ? string.Empty : ": " + failed.Detail;
                return Reply(ReportStatus.Failed, gitError, $"{gitError}: {failed.Step}{detail}\n", started);
            }

            baseSha = ((PrepareResult.Prepared)prepared).BaseSha;
            defaultBranch = ((PrepareResult.Prepared)prepared).DefaultBranch;
        }

        var minutes = fields.TimeoutMinutes ?? policy.DefaultTimeoutMinutes;

        // Spec 15: the rules travel as hook arguments, never through a file the model can reach; the blocked-calls file starts empty.
        var blockedFile = TranscriptPaths.BlockedFor(nodeDir, job.Header.Tenant, job.Header.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(blockedFile)!);
        File.Delete(blockedFile);
        List<string> hookArgs = ["hook", "pre-tool-use", "--root", runDirectory, "--blocked-file", blockedFile];
        foreach (var rule in hookRules)
        {
            hookArgs.AddRange(["--allow", rule]);
        }

        // Spec 58 decision B: on a work machine the hook refuses every command that names an outlook action.
        if (policy.WorkMail)
        {
            hookArgs.Add("--work-mail");
        }

        // Spec 06 "ModelRunRequest for a job": no tool set, extra directory, schema, model or system prompt; the project's own
        // hooks, skills, MCP servers and memory apply (Isolation.None, ex-Q7).
        var timeout = _options.TimeoutOverride ?? TimeSpan.FromMinutes(minutes);

        // Spec 59 slice 6 (AC-9): the job's own turn limit, never above the node's; a lowered value is said in the report body.
        var maxTurns = Math.Min(fields.MaxTurns ?? policy.MaxTurns, policy.MaxTurns);
        var loweredLine = fields.MaxTurns > policy.MaxTurns ? string.Create(CultureInfo.InvariantCulture, $"max_turns lowered to {maxTurns}") : null;
        var request = new ModelRunRequest(_prompt.Render(job, runDirectory, allowedTools), runDirectory, timeout)
        {
            MaxTurns = maxTurns,
            AllowedTools = allowedTools,
            MaxBudgetUsd = policy.MaxBudgetUsd,
            Isolation = ModelSessionIsolation.None,
            PreToolUseHook = new ModelHookCommand(hookExecutable, hookArgs),
            // Founding spec §6 Invocation (amended by deliverable 12, D5): a job run is unattended — the memory and context writers do nothing.
            // Deliverable 18: the run also learns which job it is, in which project, and when it times out, so `zyggy ask` can name it and stop waiting in time.
            // Spec 58 slice 5: and where its work mail marker lives (this node's directory), so the outlook tools cannot be pointed elsewhere.
            // A job that is not a mail job gets an empty marker: the outlook read tools refuse it (owner correction 2026-11-23).
            Environment = WithHooksOff(_options.ModelEnvironment, job.Header.Id, ProjectName.Derive(WorkHookInput.LastSegment(project)), _clock.GetUtcNow() + timeout, mailJob ? Outlook.OutlookJobRun.MarkerPath(nodeDir, job.Header.Tenant, job.Header.Id) : string.Empty),
            TranscriptPath = TranscriptPaths.For(nodeDir, job.Header.Tenant, job.Header.Id),
        };

        LogStarted(id, runDirectory, fields.Agent ?? "none", minutes);
        var result = await _model.RunAsync(request, ct).ConfigureAwait(false);
        var reply = Map(id, result, minutes, started) with { MaxTurns = maxTurns };

        // Spec 58 slice 5: a job that read work mail has its report screened on this laptop before it is written to the bus.
        reply = Outlook.WorkMailReportScreen.Apply(reply, nodeDir, job.Header.Tenant, job.Header.Id);
        var hasResultLines = false;
        var description = string.IsNullOrEmpty(fields.Description) ? null : fields.Description;
        string? behindLine = null;
        if (baseSha is not null)
        {
            var machine = job.Header.To.Value;
            // Spec 53: Finish learns the run's outcome, the job's branch (an empty branch counts as absent) and its description; spec 18 its base.
            var identity = new GitIdentity("zyggy (" + machine + ")", machine + "@zyggy.org");
            var named = string.IsNullOrEmpty(fields.Branch) ? null : fields.Branch;
            var outcome = await worktrees.FinishAsync(project, runDirectory, id, baseSha, identity, reply.Status, named, description, defaultBranch, fields.Base, ct)
                .ConfigureAwait(false);
            reply = WithWorktree(id, reply, outcome);
            hasResultLines = outcome.ResultLines is not null;
            behindLine = outcome.BehindLine;
        }

        // Spec 53 Report body lines: after the branch lines, "description: <text>" when the job has one, then the "behind" line; spec 59
        // slice 6 adds "max_turns lowered to <n>" last.
        string[] trailing = [.. new[] { description is null ? null : "description: " + description, behindLine, loweredLine }.OfType<string>()];
        if (trailing.Length > 0)
        {
            reply = reply with { Body = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(reply.Body.Span) + string.Join('\n', trailing) + "\n") };
            hasResultLines = true;
        }

        reply = WithBlocked(id, reply, blockedFile, hasResultLines);
        LogFinished(id, reply, result);
        return reply;
    }

    // Spec 59 Failure modes: the path's shape first (schema_unsupported: fetch_path), then fetch origin, the blob's size against
    // jobs.fetchMaxBytes (schema_unsupported: too large), then its bytes; git's first error line on a missing file (git_error: fetch).
    private async Task<JobReply> FetchAsync(Envelope.Envelope job, JobFields fields, string project, int maxBytes, DateTimeOffset started, CancellationToken ct)
    {
        var id = job.Header.Id.Value;
        var schema = RunFailureReasonWire.ToWire(RunFailureReason.SchemaUnsupported);
        var gitError = RunFailureReasonWire.ToWire(RunFailureReason.GitError);
        var path = fields.FetchPath;
        if (!FetchPaths.IsSafe(path))
        {
            LogRejected(id, schema, "fetch_path");
            return Reply(ReportStatus.Rejected, schema, $"{schema}: fetch_path\n", started);
        }

        using var projectLock = ProjectLock.TryAcquire(project, job.Header.Id, ProjectLock.CurrentProcessId, _clock.GetUtcNow());
        if (projectLock is null)
        {
            var lockPath = ProjectLock.PathOf(project);
            LogLocked(id, lockPath);
            return Reply(ReportStatus.Failed, RunFailureReasonWire.ToWire(RunFailureReason.Locked), $"locked: {lockPath}\n", started);
        }

        var branch = fields.FetchRef
            ?? _registry.Current?.Projects.FirstOrDefault(p => string.Equals(p.Name, fields.Project, StringComparison.Ordinal))?.DefaultBranch
            ?? await _git.DefaultBranchAsync(project, ct).ConfigureAwait(false)
            ?? "main";
        var revision = "origin/" + branch;
        if (!(await _git.FetchAsync(project, ct).ConfigureAwait(false)).Succeeded)
        {
            LogFetchFailed(id);
        }

        var (size, sizeResult) = await _git.BlobSizeAsync(project, revision, path!, ct).ConfigureAwait(false);
        if (size is null)
        {
            var line = FirstLine(sizeResult.Stderr) ?? "cat-file failed";
            LogWorktreeFailed(id, "fetch");
            return Reply(ReportStatus.Failed, gitError, $"{gitError}: fetch: {line}\n", started);
        }

        if (size > maxBytes)
        {
            LogRejected(id, schema, "too large");
            return Reply(ReportStatus.Rejected, schema, $"{schema}: too large\n", started);
        }

        var temporary = Path.Combine(Path.GetTempPath(), "zyggy-fetch-" + id + ".bin");
        byte[] bytes;
        try
        {
            var read = await _git.ReadBlobToFileAsync(project, revision, path!, temporary, ct).ConfigureAwait(false);
            if (!read.Succeeded)
            {
                LogWorktreeFailed(id, "fetch");
                return Reply(ReportStatus.Failed, gitError, $"{gitError}: fetch: {FirstLine(read.Stderr) ?? "cat-file failed"}\n", started);
            }

            bytes = await File.ReadAllBytesAsync(temporary, ct).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(temporary);
        }

        var text = Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal);
        var summary = ReportSummary.Truncate(text);
        var body = $"fetched {path} at {revision} ({bytes.Length.ToString(CultureInfo.InvariantCulture)} bytes)\n\n{summary}{(summary.EndsWith('\n') ? string.Empty : "\n")}";
        var name = Path.GetFileName(path!);
        LogFetched(id, path!, revision, bytes.Length);
        return Reply(ReportStatus.Done, null, body, started) with { Attachment = new ReportAttachment(name, bytes) };
    }

    // Spec 59 slice 2 Failure modes: a name this node's node.json does not list is schema_unsupported before any process starts; the
    // argv is the entry's, never text of the envelope, run in the project folder without a shell; exit 0 is done, a non-zero exit
    // command_failed with the exit code and the output's tail in the body; the full output (capped at jobs.fetchMaxBytes) attached.
    private async Task<JobReply> CommandAsync(Envelope.Envelope job, JobFields fields, string project, NodePolicy policy, DateTimeOffset started, CancellationToken ct)
    {
        var id = job.Header.Id.Value;
        var name = fields.Command ?? string.Empty;
        if (!policy.Commands.TryGetValue(name, out NodeCommand? entry) || entry.Argv.Count == 0)
        {
            var schema = RunFailureReasonWire.ToWire(RunFailureReason.SchemaUnsupported);
            LogRejected(id, schema, "unknown command");
            return Reply(ReportStatus.Rejected, schema, $"{schema}: unknown command {name}\n", started);
        }

        using var projectLock = ProjectLock.TryAcquire(project, job.Header.Id, ProjectLock.CurrentProcessId, _clock.GetUtcNow());
        if (projectLock is null)
        {
            var lockPath = ProjectLock.PathOf(project);
            LogLocked(id, lockPath);
            return Reply(ReportStatus.Failed, RunFailureReasonWire.ToWire(RunFailureReason.Locked), $"locked: {lockPath}\n", started);
        }

        var minutes = entry.TimeoutMinutes ?? Math.Min(policy.DefaultTimeoutMinutes, 30);
        var spec = new ProcessSpec(entry.Argv[0], [.. entry.Argv.Skip(1)], project) { Timeout = TimeSpan.FromMinutes(minutes) };
        LogCommandStarted(id, name, minutes);
        // IProcessRunner never throws for a program that cannot start; it returns StartFailed (decided by Central on 2026-11-21).
        var result = await _processes.RunAsync(spec, ct).ConfigureAwait(false);
        if (result.StartFailed)
        {
            var failed = RunFailureReasonWire.ToWire(RunFailureReason.CommandFailed);
            var error = result.Stderr.Trim();
            var why = error.Length > 0 ? error : $"program {entry.Argv[0]} not found or not executable";
            LogCommandFinished(id, name, "not started");
            return Reply(ReportStatus.Failed, failed, $"{failed}: command {name} did not start: {why}\n", started);
        }

        var output = (result.Stdout + result.Stderr).Replace("\r\n", "\n", StringComparison.Ordinal);
        var bytes = Encoding.UTF8.GetBytes(output);
        if (bytes.Length > policy.FetchMaxBytes)
        {
            bytes = bytes[..policy.FetchMaxBytes];
        }

        var tail = output.Length > ReportSummary.MaxCharacters ? "…" + output[^ReportSummary.MaxCharacters..] : output;
        var text = tail.Length == 0 || tail.EndsWith('\n') ? tail : tail + "\n";
        var attachment = new ReportAttachment("output.txt", bytes);
        if (result.TimedOut)
        {
            var timeout = RunFailureReasonWire.ToWire(RunFailureReason.Timeout);
            LogCommandFinished(id, name, timeout);
            return Reply(ReportStatus.Timeout, timeout, $"timeout after {minutes} min: command {name}\n\n{text}", started) with { Attachment = attachment };
        }

        var code = result.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
        if (result.ExitCode != 0)
        {
            var failed = RunFailureReasonWire.ToWire(RunFailureReason.CommandFailed);
            LogCommandFinished(id, name, "exit " + code);
            return Reply(ReportStatus.Failed, failed, $"{failed}: command {name} exited {code}\n\n{text}", started) with { Attachment = attachment };
        }

        LogCommandFinished(id, name, "exit 0");
        return Reply(ReportStatus.Done, null, $"command {name} exited 0\n\n{text}", started) with { Attachment = attachment };
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "job {Id}: command {Name} started (timeout {Minutes} min)")]
    private partial void LogCommandStarted(string id, string name, int minutes);

    [LoggerMessage(Level = LogLevel.Information, Message = "job {Id}: command {Name} finished: {Outcome}")]
    private partial void LogCommandFinished(string id, string name, string outcome);

    private static string? FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

    [LoggerMessage(Level = LogLevel.Information, Message = "job {Id}: fetched {Path} at {Revision} ({Bytes} bytes)")]
    private partial void LogFetched(string id, string path, string revision, int bytes);

    // Spec 53 Prepare steps 0–1: the job's branch, whether it also carries spec 18's base or mandate (read as front-matter keys, so the
    // rule holds before and after spec 18's parser knows them), and its project's default_branch in this node's own registry snapshot
    // (looked up by the job's project name; none for a project given by path, before the first scan, or an entry without one).
    private NamedBranch? NamedBranchOf(Envelope.Envelope job, JobFields fields)
    {
        if (string.IsNullOrEmpty(fields.Branch))
        {
            return null;
        }

        var keys = job.FrontMatter.Entries;
        var registryDefault = _registry.Current?.Projects
            .FirstOrDefault(p => string.Equals(p.Name, fields.Project, StringComparison.Ordinal))?.DefaultBranch;
        return new NamedBranch(fields.Branch, keys.ContainsKey("base") || keys.ContainsKey("mandate"), registryDefault);
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

    // Spec 15 (review of 2026-11-14): the hook's self-test. `zyggy hook pre-tool-use` with no rules and no input must answer exit 2
    // and writes no file; a binary that exists but allows, crashes, hangs or cannot start would let every tool call through.
    private Task<bool> HookRefusesAnEmptyCallAsync(string hookExecutable, CancellationToken ct) =>
        HookRefusesAnEmptyCallAsync(_processes, hookExecutable, ct);

    /// <summary>The hook's self-test for every run that relies on it: a job run, and Central's supervisor run (deliverable 18).</summary>
    internal static async Task<bool> HookRefusesAnEmptyCallAsync(IProcessRunner processes, string hookExecutable, CancellationToken ct)
    {
        try
        {
            var spec = new ProcessSpec(hookExecutable, ["hook", "pre-tool-use"], Path.GetDirectoryName(hookExecutable) ?? Environment.CurrentDirectory)
            {
                Timeout = HookSelfTestTimeout,
            };
            var result = await processes.RunAsync(spec, ct).ConfigureAwait(false);
            return result.ExitCode == 2;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Fail closed: whatever keeps the self-test from answering, the job does not run.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
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

        // Spec 59 (AC-3): a REPORT longer than the summary travels whole as report.md.
        var whole = ReportSummary.ExtractWhole(result);
        var attachment = whole.Length > ReportSummary.MaxCharacters ? new ReportAttachment("report.md", Encoding.UTF8.GetBytes(whole)) : null;
        return reply with
        {
            Attachment = attachment,
            CostUsd = result.CostUsd,
            NumTurns = result.NumTurns,
            Model = result.Model,
            InputTokens = Clamp(result.InputTokens),
            OutputTokens = Clamp(result.OutputTokens),
            CacheWriteTokens = Clamp(result.CacheWriteTokens),
            CacheReadTokens = Clamp(result.CacheReadTokens),
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

    /// <summary>
    /// The model environment with <c>ZYGGY_HOOKS=off</c> and the job's id, project and end time set last, so no configured value can
    /// switch the hooks back on (spec 12 A5) or name another job (deliverable 18).
    /// </summary>
    private static Dictionary<string, string> WithHooksOff(IReadOnlyDictionary<string, string> environment, EnvelopeId job, string project, DateTimeOffset ends, string workMailMarker) =>
        new(environment, StringComparer.Ordinal)
        {
            ["ZYGGY_HOOKS"] = "off",
            ["ZYGGY_JOB"] = job.Value,
            ["ZYGGY_JOB_PROJECT"] = project,
            ["ZYGGY_JOB_ENDS"] = ends.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            [Outlook.OutlookJobRun.MarkerVariable] = workMailMarker,
        };

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "job {Id}: rejected: branch_refused ({Rule})")]
    private partial void LogBranchRefused(string id, string rule);

    [LoggerMessage(Level = LogLevel.Information, Message = "job {Id}: mail job on a work machine: {Dropped} rule(s) beyond the outlook read verbs and file reads dropped")]
    private partial void LogMailJobNarrowed(string id, int dropped);

    [LoggerMessage(Level = LogLevel.Warning, Message = "job {Id}: worktree git step failed: {Step}")]
    private partial void LogWorktreeFailed(string id, string step);
}
