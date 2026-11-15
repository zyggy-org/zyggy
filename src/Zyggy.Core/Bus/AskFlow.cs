using System.Globalization;
using System.Text;

using Microsoft.Extensions.Logging;

using Zyggy.Core.Configuration;
using Zyggy.Core.Envelope;
using Zyggy.Core.Registry;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Bus;

/// <summary>What <c>zyggy ask</c> was given (deliverable 18, "Asking" and "Waiting").</summary>
/// <param name="Folder">The folder the agent works in; its last segment names the project of a session.</param>
/// <param name="Text">The question.</param>
/// <param name="Default">One line: what the agent does when no answer comes.</param>
/// <param name="Options">The options the agent sees.</param>
/// <param name="WaitMinutes">How long to wait for the decision, 1–60; <see langword="null"/> for the default (5 in a job, 10 in a session).</param>
/// <param name="Job">The job run's id (<c>ZYGGY_JOB</c>), when a job asks.</param>
/// <param name="JobProject">The job's project (<c>ZYGGY_JOB_PROJECT</c>).</param>
/// <param name="JobEnds">When the job run times out (<c>ZYGGY_JOB_ENDS</c>, UTC); the wait never goes beyond it.</param>
public sealed record AskRequest(
    string Folder,
    string Text,
    string Default,
    IReadOnlyList<string> Options,
    int? WaitMinutes = null,
    string? Job = null,
    string? JobProject = null,
    string? JobEnds = null);

/// <summary>How a <c>zyggy ask</c> ended.</summary>
public abstract record AskOutcome
{
    private protected AskOutcome()
    {
    }
}

/// <summary>Central's decision reached this machine within the wait.</summary>
/// <param name="Question">The question's id.</param>
/// <param name="Decision">The decision.</param>
public sealed record AskAnswered(EnvelopeId Question, DecisionRecord Decision) : AskOutcome;

/// <summary>No decision came within the wait, or nothing was asked on purpose: the agent follows its default.</summary>
/// <param name="Question">The question's id, when one was written.</param>
/// <param name="Default">The default line the agent stated.</param>
/// <param name="Why">Why nothing was asked or the question did not leave this machine; <see langword="null"/> when the wait simply ran out.</param>
public sealed record AskUnanswered(EnvelopeId? Question, string Default, string? Why) : AskOutcome;

/// <summary>The question was refused before anything was signed or written.</summary>
/// <param name="Detail">Why, in one line; never the refused text.</param>
/// <param name="Usage"><see langword="true"/> for a wrong call (no live session, a wait out of range); <see langword="false"/> for a secret pattern or a size limit.</param>
public sealed record AskRefused(string Detail, bool Usage) : AskOutcome;

/// <summary>Signing or the bus write failed; nothing is on the bus.</summary>
/// <param name="Detail">The failure text.</param>
public sealed record AskFailed(string Detail) : AskOutcome;

/// <summary>
/// The asking side of "Questions and decisions" (deliverable 18): names the asker (the job run, or the live session of the project
/// that was heard from last), writes the question through <see cref="QuestionAsker"/>, then pulls the bus every 20 seconds until
/// Central's decision is there or the wait ran out. A question whose decision came after the wait is remembered under the node
/// directory and handed out once, at the next session start in that project. Never throws except for the caller's cancellation.
/// </summary>
public sealed partial class AskFlow
{
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    private const int JobWaitMinutes = 5;

    private const int SessionWaitMinutes = 10;

    private const int MaxWaitMinutes = 60;

    private static readonly TimeSpan PullEvery = TimeSpan.FromSeconds(20);

    private readonly Func<QuestionAsker> _asker;
    private readonly BusRepository _bus;
    private readonly BusInventory _inventory;
    private readonly WorkSessionStore _sessions;
    private readonly AskedStore _asked;
    private readonly SupervisionSettings _settings;
    private readonly TimeProvider _clock;
    private readonly ILogger<AskFlow> _log;

    internal AskFlow(Func<QuestionAsker> asker, BusRepository bus, BusInventory inventory, WorkSessionStore sessions, AskedStore asked, SupervisionSettings settings, TimeProvider clock, ILogger<AskFlow> log)
    {
        _asker = asker ?? throw new ArgumentNullException(nameof(asker));
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _asked = asked ?? throw new ArgumentNullException(nameof(asked));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>Asks Central and waits for its decision.</summary>
    /// <param name="request">The question and who asks.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see cref="AskAnswered"/>, <see cref="AskUnanswered"/>, <see cref="AskRefused"/> or <see cref="AskFailed"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is null.</exception>
    public async Task<AskOutcome> AskAsync(AskRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.WaitMinutes is < 1 or > MaxWaitMinutes)
        {
            return new AskRefused("--wait must be 1 to 60 minutes", Usage: true);
        }

        if (!_settings.Enabled)
        {
            return new AskUnanswered(null, request.Default, "supervision disabled");
        }

        EnvelopeId? job = null;
        string? session = null;
        string project;
        if (request.Job is not null)
        {
            if (!EnvelopeId.TryParse(request.Job, out job) || string.IsNullOrEmpty(request.JobProject))
            {
                return new AskRefused("ZYGGY_JOB and ZYGGY_JOB_PROJECT do not name a job run", Usage: true);
            }

            project = request.JobProject;
        }
        else
        {
            project = ProjectName.Derive(WorkHookInput.LastSegment(request.Folder));
            session = project.Length == 0 ? null : LatestSession(project);
            if (session is null)
            {
                return new AskRefused("no live session of this project on this machine, and no job run", Usage: true);
            }
        }

        AskDecideResult asked;
        try
        {
            asked = await _asker().AskAsync(new QuestionRequest(project, request.Text, request.Default, request.Options, session, job), ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return new AskFailed(ex.Message);
        }

        switch (asked)
        {
            case AskDecideRefused refused:
                return new AskRefused(refused.Field + ": " + refused.Detail, Usage: false);
            case AskDecideSkipped skipped:
                return new AskUnanswered(null, request.Default, skipped.Why);
            case AskDecidePublished published:
                Remember(published.Id, project);
                DateTimeOffset now = _clock.GetUtcNow();
                DateTimeOffset until = now + TimeSpan.FromMinutes(request.WaitMinutes ?? (job is null ? SessionWaitMinutes : JobWaitMinutes));
                if (job is not null && TryTimestamp(request.JobEnds, out DateTimeOffset ends) && ends < until)
                {
                    until = ends;
                }

                return await WaitAsync(published, request.Default, until, ct).ConfigureAwait(false);
            case AskDecideFailed failed:
                return new AskFailed(failed.Stage + ": " + failed.Detail);
            default:
                return new AskFailed(asked.GetType().Name);
        }
    }

    /// <summary>
    /// The decisions that came after the wait of a question asked in the project of this session's folder: each is returned once,
    /// then forgotten. Reads the checkout as it is; the session-start status write pulled it just before.
    /// </summary>
    /// <param name="hookJson">The <c>SessionStart</c> hook's standard input.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The late decisions, oldest question first; empty when there is none or the input cannot be read.</returns>
    public async Task<IReadOnlyList<DecisionRecord>> LateDecisionsAsync(string? hookJson, CancellationToken ct)
    {
        if (WorkHookInput.Read(hookJson) is not { } input)
        {
            return [];
        }

        string project = ProjectName.Derive(WorkHookInput.LastSegment(input.Cwd));
        var late = new List<DecisionRecord>();
        try
        {
            foreach ((EnvelopeId question, string askedIn) in _asked.Pending(_clock.GetUtcNow()))
            {
                if (askedIn == project && await _inventory.DecisionForAsync(question, ct).ConfigureAwait(false) is { } decision)
                {
                    _asked.Remove(question);
                    late.Add(decision);
                }
            }
        }
        catch (Exception ex) when (ex is BusBusyException or IOException or UnauthorizedAccessException)
        {
            LogNotRead(ex.Message);
        }

        return late;
    }

    private static bool TryTimestamp(string? text, out DateTimeOffset value)
    {
        value = default;
        return text is not null
            && DateTimeOffset.TryParseExact(text, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);
    }

    private async Task<AskOutcome> WaitAsync(AskDecidePublished published, string @default, DateTimeOffset until, CancellationToken ct)
    {
        while (true)
        {
            await _bus.PullAsync(ct).ConfigureAwait(false);
            try
            {
                if (await _inventory.DecisionForAsync(published.Id, ct).ConfigureAwait(false) is { } decision)
                {
                    Forget(published.Id);
                    return new AskAnswered(published.Id, decision);
                }
            }
            catch (BusBusyException)
            {
                // another zyggy process holds the checkout; the next round looks again
            }

            TimeSpan left = until - _clock.GetUtcNow();
            if (left <= TimeSpan.Zero)
            {
                return new AskUnanswered(published.Id, @default, published.Pushed ? null : "the question has not left this machine yet");
            }

            await Task.Delay(left < PullEvery ? left : PullEvery, _clock, ct).ConfigureAwait(false);
        }
    }

    private string? LatestSession(string project)
    {
        try
        {
            return _sessions.LatestSession(project);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogNotRead(ex.Message);
            return null;
        }
    }

    private void Remember(EnvelopeId question, string project)
    {
        try
        {
            _asked.Add(question, project);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogNotRead(ex.Message);
        }
    }

    private void Forget(EnvelopeId question)
    {
        try
        {
            _asked.Remove(question);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogNotRead(ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "asked-questions state not usable: {Detail}")]
    private partial void LogNotRead(string detail);
}

/// <summary>
/// The questions this machine asked and has not seen answered, under <c>&lt;nodeDir&gt;/work/&lt;tenant&gt;/asked/&lt;question id&gt;</c>, each file
/// holding the project's name. Never on the bus. An entry older than 30 days is dropped.
/// </summary>
internal sealed class AskedStore
{
    private static readonly TimeSpan KeepFor = TimeSpan.FromDays(30);

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _directory;

    public AskedStore(string nodeDir, TenantId tenant)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeDir);
        ArgumentNullException.ThrowIfNull(tenant);
        _directory = Path.Join(nodeDir, "work", tenant.Value, "asked");
    }

    public void Add(EnvelopeId question, string project)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Join(_directory, question.Value), project + "\n", Utf8);
    }

    public void Remove(EnvelopeId question) => File.Delete(Path.Join(_directory, question.Value));

    /// <summary>The open entries, oldest question first; entries past their age are deleted on the way.</summary>
    public IReadOnlyList<(EnvelopeId Question, string Project)> Pending(DateTimeOffset now)
    {
        if (!Directory.Exists(_directory))
        {
            return [];
        }

        var pending = new List<(EnvelopeId Question, string Project)>();
        foreach (string file in Directory.EnumerateFiles(_directory).Order(StringComparer.Ordinal))
        {
            if (!EnvelopeId.TryParse(Path.GetFileName(file), out EnvelopeId? question))
            {
                continue;
            }

            if (now - question.Timestamp > KeepFor)
            {
                File.Delete(file);
                continue;
            }

            pending.Add((question, File.ReadAllText(file, Utf8).Trim()));
        }

        return pending;
    }
}
