using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using Zyggy.Core.Configuration;
using Zyggy.Core.Envelope;
using Zyggy.Core.Git;
using Zyggy.Core.Memory;
using Zyggy.Core.Registry;
using Zyggy.Core.Supervision;
using Zyggy.Core.Tenancy;


namespace Zyggy.Core.Bus;

/// <summary>How a session-task step ended (deliverable 18, "Work into a live session").</summary>
public enum SessionTaskResult
{
    /// <summary>A task was claimed; <see cref="SessionTaskOutcome.Output"/> is the Stop-hook answer.</summary>
    Delivered,

    /// <summary>Nothing to deliver at this turn end.</summary>
    Nothing,

    /// <summary>The report is on the remote.</summary>
    Reported,

    /// <summary>The report is committed and waits for the next push.</summary>
    Deferred,

    /// <summary>The id is not a ULID, or the line breaks its rule (usage).</summary>
    Refused,

    /// <summary>The <c>--why</c> line matches a secret pattern.</summary>
    RefusedBySecretPattern,

    /// <summary>No task with that id is open in this machine's sessions.</summary>
    NotOpen,

    /// <summary>A git, bus or file step failed; <see cref="SessionTaskOutcome.Detail"/> says which.</summary>
    Failed,
}

/// <summary>The outcome of a session-task step.</summary>
/// <param name="Result">What happened.</param>
/// <param name="Output">The Stop-hook answer for <see cref="SessionTaskResult.Delivered"/>; otherwise <see langword="null"/>.</param>
/// <param name="Detail">Why, for a refusal or a failure.</param>
public sealed record SessionTaskOutcome(SessionTaskResult Result, string? Output = null, string? Detail = null);

/// <summary>
/// A task Central delivers into a live session (deliverable 18, "Work into a live session"). At every turn end — not on a continuation
/// (<c>stop_hook_active</c>), not while a task is open, not while the owner's stop marker is there — it pulls, takes the oldest verified
/// <c>deliver: session</c> job of this machine for the session's project whose mandate allows it (<see cref="SessionTaskRules.Refusal"/>),
/// claims it and returns the Stop-hook answer with the fixed frame. <see cref="FinishAsync"/> writes the report and removes the claim.
/// The open task is kept in the node directory; it never throws but on cancellation.
/// </summary>
public sealed partial class SessionTaskHook
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly BusRepository _bus;
    private readonly BusInventory _inventory;
    private readonly GitClient _git;
    private readonly Func<SecretPatterns> _patterns;
    private readonly Func<KeyId> _signWith;
    private readonly SupervisionSettings _settings;
    private readonly string _nodeDir;
    private readonly string _stateFile;
    private readonly TimeProvider _clock;
    private readonly ILogger<SessionTaskHook> _log;

    internal SessionTaskHook(
        BusRepository bus,
        BusInventory inventory,
        GitClient git,
        Func<SecretPatterns> patterns,
        Func<KeyId> signWith,
        SupervisionSettings settings,
        string nodeDir,
        TenantId tenant,
        TimeProvider clock,
        ILogger<SessionTaskHook> log)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _git = git ?? throw new ArgumentNullException(nameof(git));
        _patterns = patterns ?? throw new ArgumentNullException(nameof(patterns));
        _signWith = signWith ?? throw new ArgumentNullException(nameof(signWith));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _nodeDir = nodeDir ?? throw new ArgumentNullException(nameof(nodeDir));
        ArgumentNullException.ThrowIfNull(tenant);
        _stateFile = Path.Join(nodeDir, "work", tenant.Value, "session-task.json");
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>The turn-end step: claims at most one task and returns the Stop-hook answer.</summary>
    /// <param name="hookJson">The Stop hook's JSON.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    public async Task<SessionTaskOutcome> TurnEndAsync(string? hookJson, CancellationToken ct)
    {
        if (!_settings.Enabled || SupervisionSwitch.IsStopped(_nodeDir))
        {
            return new SessionTaskOutcome(SessionTaskResult.Nothing);
        }

        StopHookEnvelope? envelope = HookJsonContext.Deserialize<StopHookEnvelope>(hookJson ?? string.Empty);
        if (envelope is null || envelope.StopHookActive == true || WorkHookInput.Read(hookJson) is not { Cwd.Length: > 0 } input || Load() is not null)
        {
            return new SessionTaskOutcome(SessionTaskResult.Nothing);
        }

        try
        {
            BusSyncResult pulled = await _bus.PullAsync(ct).ConfigureAwait(false);
            if (!pulled.Succeeded)
            {
                // An unreachable bus is the status hook's line, once per streak; no task this turn, none queued.
                return new SessionTaskOutcome(SessionTaskResult.Nothing, Detail: "pull: " + pulled.Detail);
            }

            string project = ProjectName.Derive(WorkHookInput.LastSegment(input.Cwd));
            DateTimeOffset now = _clock.GetUtcNow();
            foreach (BusFile file in await _bus.ListNewJobsAsync(ct).ConfigureAwait(false))
            {
                if (await _bus.VerifyIntakeAsync(file, ct).ConfigureAwait(false) is not AcceptedJob { Job.Job: { Deliver: JobDelivery.Session, Mandate: { } mandate } } job)
                {
                    continue;
                }

                MandateLedger ledger = await _inventory.MandateLedgerAsync(mandate, ct).ConfigureAwait(false);
                if (SessionTaskRules.Refusal(ledger, job.Job, _bus.Me.Machine, project, now) is not null)
                {
                    continue;
                }

                string? head = await _git.RevParseAsync(input.Cwd, "HEAD", ct).ConfigureAwait(false);
                WriteOutcome claimed = await _bus.ClaimAsync(job, ct).ConfigureAwait(false);
                if (claimed is WriteFailed failed)
                {
                    return new SessionTaskOutcome(SessionTaskResult.Failed, Detail: "claim: " + failed.Stage + ": " + failed.Detail);
                }

                Save(new OpenTask(file.Id.Value, input.Session, input.Cwd, head));
                LogDelivered(file.Id.Value, input.Session);
                return new SessionTaskOutcome(SessionTaskResult.Delivered, SessionTaskRules.StopJson(SessionTaskRules.Frame(job.Job)));
            }

            return new SessionTaskOutcome(SessionTaskResult.Nothing);
        }
        catch (Exception ex) when (ex is BusBusyException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new SessionTaskOutcome(SessionTaskResult.Failed, Detail: ex.Message);
        }
    }

    /// <summary><c>zyggy work --done</c> / <c>--failed</c>: writes the report on the open task and removes its claim.</summary>
    /// <param name="idText">The task's id as typed.</param>
    /// <param name="done">Whether the session finished it.</param>
    /// <param name="why">The <c>--why</c> line, or <see langword="null"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    public async Task<SessionTaskOutcome> FinishAsync(string? idText, bool done, string? why, CancellationToken ct)
    {
        if (!EnvelopeId.TryParse(idText, out EnvelopeId? id))
        {
            return new SessionTaskOutcome(SessionTaskResult.Refused, Detail: "the task id must be a ULID");
        }

        if (why is not null)
        {
            if (SessionTaskRules.WhyProblem(why) is { } problem)
            {
                return new SessionTaskOutcome(SessionTaskResult.Refused, Detail: "--why: " + problem);
            }

            if (_patterns().TryMatch(why, out string? pattern))
            {
                return new SessionTaskOutcome(SessionTaskResult.RefusedBySecretPattern, Detail: "--why: matches secret pattern " + pattern);
            }
        }

        if (Load() is not { } open || !string.Equals(open.Id, id.Value, StringComparison.Ordinal))
        {
            return new SessionTaskOutcome(SessionTaskResult.NotOpen, Detail: "no open session task " + id.Value + " on this machine");
        }

        try
        {
            var file = new BusFile(BusPaths.EnvelopeFile(BusPaths.Claimed(_bus.Me.Tenant, _bus.Me.Machine), id), id);
            if (!File.Exists(BusPaths.Resolve(_bus.CheckoutRoot, file.RelativePath))
                || await _bus.VerifyClaimedAsync(file, ct).ConfigureAwait(false) is not AcceptedJob { Job.Job: { } fields } claimed)
            {
                Delete();
                return new SessionTaskOutcome(SessionTaskResult.NotOpen, Detail: "the claim of " + id.Value + " is gone (the sweep may have put it back)");
            }

            string? head = await _git.RevParseAsync(open.Folder, "HEAD", ct).ConfigureAwait(false);
            int changed = await ChangedFilesAsync(open, ct).ConfigureAwait(false);
            string body = SessionTaskRules.Body(done, changed, head, why, _settings.Detail == SupervisionDetail.Fixed);
            // A failed report needs a reason (§4); the closed list's claude_error is the session's own failure, the why line says which.
            var report = new ReportFields(done ? ReportStatus.Done : ReportStatus.Failed, done ? null : "claude_error", Mandate: fields.Mandate, Step: fields.Step);
            WriteOutcome outcome = await _bus.ReportAsync(claimed, report, Encoding.UTF8.GetBytes(body), _signWith(), ct).ConfigureAwait(false);
            switch (outcome)
            {
                case WriteFailed failed:
                    return new SessionTaskOutcome(SessionTaskResult.Failed, Detail: failed.Stage + ": " + failed.Detail);
                case Deferred:
                    Delete();
                    return new SessionTaskOutcome(SessionTaskResult.Deferred);
                default:
                    Delete();
                    return new SessionTaskOutcome(SessionTaskResult.Reported);
            }
        }
        catch (Exception ex) when (ex is BusBusyException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new SessionTaskOutcome(SessionTaskResult.Failed, Detail: ex.Message);
        }
    }

    // The files that differ from the head at claim — committed since, changed, or new and not ignored. 0 when git cannot say.
    private async Task<int> ChangedFilesAsync(OpenTask open, CancellationToken ct)
    {
        var files = new HashSet<string>(StringComparer.Ordinal);
        if (open.Head is not null)
        {
            GitResult diff = await _git.RunAsync(open.Folder, ["diff", "--name-only", open.Head], null, ct).ConfigureAwait(false);
            if (diff.Succeeded)
            {
                files.UnionWith(diff.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
        }

        GitResult untracked = await _git.RunAsync(open.Folder, ["ls-files", "--others", "--exclude-standard"], null, ct).ConfigureAwait(false);
        if (untracked.Succeeded)
        {
            files.UnionWith(untracked.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        return files.Count;
    }

    private OpenTask? Load()
    {
        try
        {
            return File.Exists(_stateFile) ? JsonSerializer.Deserialize<OpenTask>(File.ReadAllText(_stateFile), Json) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private void Save(OpenTask task)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);
        File.WriteAllText(_stateFile, JsonSerializer.Serialize(task, Json));
    }

    private void Delete()
    {
        if (File.Exists(_stateFile))
        {
            File.Delete(_stateFile);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "session task {Task} delivered to session {Session}")]
    private partial void LogDelivered(string task, string session);

    private sealed record OpenTask(string Id, string Session, string Folder, string? Head);
}
