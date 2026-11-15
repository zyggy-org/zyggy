using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;

using Zyggy.Core.Configuration;
using Zyggy.Core.Git;
using Zyggy.Core.Memory;
using Zyggy.Core.Registry;

namespace Zyggy.Core.Bus;

/// <summary>What a status hook call did (deliverable 18).</summary>
public enum WorkHookResult
{
    /// <summary>A status was written.</summary>
    Published,

    /// <summary>The session's last status is younger than <c>supervision.statusMinutes</c>; nothing was written.</summary>
    NotDue,

    /// <summary>The session's status was removed, or there was none.</summary>
    Removed,

    /// <summary>The step was recorded for the session's next status.</summary>
    Recorded,

    /// <summary>Nothing was written on purpose (supervision disabled, hooks off, no usable project or session name).</summary>
    Skipped,

    /// <summary>The input is not a hook JSON object with a session id.</summary>
    Unparseable,

    /// <summary>A value breaks its rule; nothing was written.</summary>
    Refused,

    /// <summary>A value matches a secret pattern; nothing was written and the value is kept nowhere.</summary>
    RefusedBySecretPattern,

    /// <summary>Signing or the bus write failed; the next turn end tries again.</summary>
    Failed,

    /// <summary>It failed again in the same streak; the failure was already reported.</summary>
    FailedAgain,
}

/// <summary>The result of a status hook call and, where there is one, the one-line reason.</summary>
/// <param name="Result">What happened.</param>
/// <param name="Detail">The reason, for a refusal, a skip or a failure.</param>
public sealed record WorkHookOutcome(WorkHookResult Result, string? Detail = null);

/// <summary>
/// The laptop side of "Live work is visible" (deliverable 18): what a Claude Code session's hooks call at session start, at the end
/// of a turn and at session end. Reads the hook JSON (<c>session_id</c>, <c>cwd</c>), keeps the session's start time and last publish
/// time under the node directory, reads branch, head, dirty count and spec from the project folder when <c>supervision.detail</c> is
/// <c>full</c>, and publishes through <see cref="WorkStatusPublisher"/> — at a turn end at most once per <c>supervision.statusMinutes</c>.
/// Never throws except for the caller's cancellation.
/// </summary>
public sealed partial class WorkStatusHook
{
    private readonly Func<WorkStatusPublisher> _publisher;
    private readonly Func<SecretPatterns> _patterns;
    private readonly GitClient _git;
    private readonly WorkSessionStore _store;
    private readonly SupervisionSettings _settings;
    private readonly TimeProvider _clock;
    private readonly ILogger<WorkStatusHook> _log;

    internal WorkStatusHook(Func<WorkStatusPublisher> publisher, Func<SecretPatterns> patterns, GitClient git, WorkSessionStore store, SupervisionSettings settings, TimeProvider clock, ILogger<WorkStatusHook> log)
    {
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _patterns = patterns ?? throw new ArgumentNullException(nameof(patterns));
        _git = git ?? throw new ArgumentNullException(nameof(git));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>Publishes the session's first status, whatever the throttle says.</summary>
    /// <param name="hookJson">The hook's standard input.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    public Task<WorkHookOutcome> SessionStartAsync(string? hookJson, CancellationToken ct) => RunAsync(hookJson, always: true, ct);

    /// <summary>Publishes the session's status when its last one is at least <c>supervision.statusMinutes</c> old.</summary>
    /// <param name="hookJson">The hook's standard input.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    public Task<WorkHookOutcome> TurnEndAsync(string? hookJson, CancellationToken ct) => RunAsync(hookJson, always: false, ct);

    /// <summary>Removes the session's status from the bus and its state from this machine.</summary>
    /// <param name="hookJson">The hook's standard input.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    public async Task<WorkHookOutcome> SessionEndAsync(string? hookJson, CancellationToken ct)
    {
        if (!_settings.Enabled)
        {
            return new WorkHookOutcome(WorkHookResult.Skipped, "supervision disabled");
        }

        if (WorkHookInput.Read(hookJson) is not { } input)
        {
            return new WorkHookOutcome(WorkHookResult.Unparseable);
        }

        try
        {
            WorkStatusResult result = await _publisher().RemoveAsync(input.Session, ct).ConfigureAwait(false);
            _store.Delete(input.Session);
            return result switch
            {
                WorkStatusRemoved => new WorkHookOutcome(WorkHookResult.Removed),
                WorkStatusSkipped skipped => new WorkHookOutcome(WorkHookResult.Skipped, skipped.Why),
                WorkStatusFailed failed => new WorkHookOutcome(WorkHookResult.Failed, failed.Stage + ": " + failed.Detail),
                _ => new WorkHookOutcome(WorkHookResult.Failed, result.GetType().Name),
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            LogFailed(input.Session, ex.Message);
            return new WorkHookOutcome(WorkHookResult.Failed, ex.Message);
        }
    }

    /// <summary>Records the step a session says it is on; it travels with the session's next status (<c>detail: full</c> only).</summary>
    /// <param name="sessionId">The session id as Claude Code gives it.</param>
    /// <param name="label">The step: 1–60 characters of <c>[a-z0-9 -]</c>.</param>
    /// <returns>The outcome.</returns>
    public WorkHookOutcome StateStep(string? sessionId, string? label)
    {
        if (WorkHookInput.SessionOf(sessionId) is not { } session)
        {
            return new WorkHookOutcome(WorkHookResult.Refused, "session: must start with 8 letters or digits");
        }

        if (label is null || !StepLabel().IsMatch(label))
        {
            return new WorkHookOutcome(WorkHookResult.Refused, "step: must be 1 to 60 lowercase letters, digits, spaces or hyphens");
        }

        try
        {
            if (_patterns().TryMatch(label, out string name))
            {
                return new WorkHookOutcome(WorkHookResult.RefusedBySecretPattern, "matches secret pattern " + name);
            }

            WorkSessionState state = _store.Load(session) ?? new WorkSessionState(_clock.GetUtcNow());
            _store.Save(session, state with { Step = label });
            return new WorkHookOutcome(WorkHookResult.Recorded);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            LogFailed(session, ex.Message);
            return new WorkHookOutcome(WorkHookResult.Failed, ex.Message);
        }
    }

    /// <summary>
    /// The spec a branch belongs to: for <c>feature/&lt;NN&gt;-…</c>, the one <c>_specs/&lt;NN&gt;-*.md</c> of the project folder; <see langword="null"/>
    /// when the branch has another shape or none or several files match.
    /// </summary>
    internal static string? SpecOf(string projectFolder, string? branch)
    {
        Match match = branch is null ? Match.Empty : FeatureBranch().Match(branch);
        string specs = Path.Join(projectFolder, "_specs");
        if (!match.Success || !Directory.Exists(specs))
        {
            return null;
        }

        string[] files = Directory.GetFiles(specs, match.Groups[1].Value + "-*.md", SearchOption.TopDirectoryOnly);
        return files.Length == 1 ? "_specs/" + Path.GetFileName(files[0]) : null;
    }

    private async Task<WorkHookOutcome> RunAsync(string? hookJson, bool always, CancellationToken ct)
    {
        if (!_settings.Enabled)
        {
            return new WorkHookOutcome(WorkHookResult.Skipped, "supervision disabled");
        }

        if (WorkHookInput.Read(hookJson) is not { } input)
        {
            return new WorkHookOutcome(WorkHookResult.Unparseable);
        }

        try
        {
            DateTimeOffset now = _clock.GetUtcNow();
            WorkSessionState? known = _store.Load(input.Session);
            WorkSessionState state = known ?? new WorkSessionState(now);
            if (!always && state.Published is { } published && now - published < TimeSpan.FromMinutes(_settings.StatusMinutes))
            {
                return new WorkHookOutcome(WorkHookResult.NotDue);
            }

            string project = ProjectName.Derive(WorkHookInput.LastSegment(input.Cwd));
            if (project.Length == 0)
            {
                return new WorkHookOutcome(WorkHookResult.Skipped, "the session's folder gives no project name");
            }

            state = state with { Project = project };
            WorkStatusRequest request = await RequestAsync(project, input, state, now, ct).ConfigureAwait(false);
            WorkStatusResult result = await _publisher().PublishAsync(request, ct).ConfigureAwait(false);
            switch (result)
            {
                case WorkStatusPublished:
                    _store.Save(input.Session, state with { Published = now, Failing = false });
                    return new WorkHookOutcome(WorkHookResult.Published);
                case WorkStatusSkipped skipped:
                    return new WorkHookOutcome(WorkHookResult.Skipped, skipped.Why);
                case WorkStatusRefused refused:
                    _store.Save(input.Session, state);
                    return new WorkHookOutcome(
                        refused.Detail.StartsWith("matches secret pattern", StringComparison.Ordinal) ? WorkHookResult.RefusedBySecretPattern : WorkHookResult.Refused,
                        refused.Field + ": " + refused.Detail);
                case WorkStatusFailed failed:
                    _store.Save(input.Session, state with { Failing = true });
                    return new WorkHookOutcome(state.Failing ? WorkHookResult.FailedAgain : WorkHookResult.Failed, failed.Stage + ": " + failed.Detail);
                default:
                    return new WorkHookOutcome(WorkHookResult.Failed, result.GetType().Name);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            LogFailed(input.Session, ex.Message);
            return new WorkHookOutcome(WorkHookResult.Failed, ex.Message);
        }
    }

    /// <summary>The facts of the session's folder; with <c>detail: fixed</c> git is never called.</summary>
    private async Task<WorkStatusRequest> RequestAsync(string project, WorkHookInput input, WorkSessionState state, DateTimeOffset now, CancellationToken ct)
    {
        if (_settings.Detail != SupervisionDetail.Full || !Directory.Exists(input.Cwd))
        {
            return new WorkStatusRequest(project, input.Session, state.Since, now);
        }

        string? branch = await _git.CurrentBranchAsync(input.Cwd, ct).ConfigureAwait(false);
        string? head = await _git.RevParseAsync(input.Cwd, "HEAD", ct).ConfigureAwait(false);
        if (head is null)
        {
            return new WorkStatusRequest(project, input.Session, state.Since, now, Step: state.Step);
        }

        int dirty = (await _git.StatusEntriesAsync(input.Cwd, ct).ConfigureAwait(false)).Count;
        return new WorkStatusRequest(project, input.Session, state.Since, now, branch, head.Length > 7 ? head[..7] : head, dirty, SpecOf(input.Cwd, branch), state.Step);
    }

    [GeneratedRegex("^feature/([0-9]{1,4})-", RegexOptions.CultureInvariant)]
    private static partial Regex FeatureBranch();

    [GeneratedRegex("^[a-z0-9 -]{1,60}$", RegexOptions.CultureInvariant)]
    private static partial Regex StepLabel();

    [LoggerMessage(Level = LogLevel.Warning, Message = "status of session {Session} not written: {Detail}")]
    private partial void LogFailed(string session, string detail);
}

/// <summary>What the status hooks read from Claude Code's hook JSON: the session's 8-character id and its folder.</summary>
/// <param name="Session">The first 8 characters of <c>session_id</c>, lowercased.</param>
/// <param name="Cwd">The session's working directory; empty when the hook did not give one.</param>
internal sealed record WorkHookInput(string Session, string Cwd)
{
    /// <summary>Reads the hook JSON; <see langword="null"/> when it is not a JSON object or its session id does not start with 8 letters or digits.</summary>
    public static WorkHookInput? Read(string? hookJson)
    {
        StopHookEnvelope? envelope = HookJsonContext.Deserialize<StopHookEnvelope>(hookJson ?? string.Empty);
        return envelope is not null && SessionOf(envelope.SessionId) is { } session ? new WorkHookInput(session, envelope.Cwd ?? string.Empty) : null;
    }

    /// <summary>The status <c>session</c> of a Claude Code session id: its first 8 characters lowercased, when they are letters or digits.</summary>
    public static string? SessionOf(string? sessionId)
    {
        if (sessionId is null || sessionId.Length < 8 || !StopHookInput.IsSafeSessionId(sessionId))
        {
            return null;
        }

        string session = sessionId[..8].ToLowerInvariant();
        return session.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9')) ? session : null;
    }

    /// <summary>The last segment of a folder path, whichever separator it uses.</summary>
    public static string LastSegment(string folder)
    {
        string trimmed = folder.Replace('\\', '/').TrimEnd('/');
        return trimmed[(trimmed.LastIndexOf('/') + 1)..];
    }
}
