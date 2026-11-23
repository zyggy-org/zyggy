using System.Globalization;

using Microsoft.Extensions.Logging;

using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;
using Zyggy.Core.Envelope;
using Zyggy.Core.Jobs;
using Zyggy.Core.Models;
using Zyggy.Core.Policy;
using Zyggy.Core.Processes;
using Zyggy.Core.Registry;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Supervision;

/// <summary>The supervision keys of one pass, taken once so a run keeps the values it started with (spec 55).</summary>
internal sealed record SupervisorKeys(string? Project, IReadOnlyList<MachineName> WorkMachines, IReadOnlyList<string> OutboundPaths);

/// <summary>What the wake needs beside its services (deliverable 18, Configuration): <c>node.json</c>'s <c>supervision</c> keys and the run's edges.</summary>
/// <param name="Project">The registry name of Central's own project (<c>supervision.project</c>); <see langword="null"/>: no run starts.</param>
/// <param name="RunsPerHour">The most runs started within 60 minutes (<c>supervision.runsPerHour</c>).</param>
/// <param name="WorkMachines">The machines answered under a read root and without the Hub tool (<c>supervision.workMachines</c>).</param>
/// <param name="OutboundPaths">The folders a run may read while it answers a work machine (<c>supervision.outboundPaths</c>).</param>
/// <param name="BusRoot">The bus checkout: the one question a work-machine run answers is read from it.</param>
/// <param name="NodeDir">The node directory: transcripts and blocked-calls files go under it, beside those of job runs.</param>
/// <param name="HookExecutable">The <c>zyggy</c> binary that is the run's tool guard.</param>
/// <param name="ModelEnvironment">Environment additions for the model process.</param>
/// <param name="TimeoutOverride">A test's run timeout; <see langword="null"/> means <c>claude.defaultTimeoutMinutes</c>.</param>
internal sealed record SupervisorWakeOptions(
    string? Project,
    int RunsPerHour,
    IReadOnlyList<MachineName> WorkMachines,
    IReadOnlyList<string> OutboundPaths,
    string BusRoot,
    string NodeDir,
    string HookExecutable,
    IReadOnlyDictionary<string, string> ModelEnvironment,
    TimeSpan? TimeoutOverride);

/// <summary>
/// Central's supervisor wake (deliverable 18, "The supervisor run"), run by the listener after every pickup pass. One pass first
/// writes the decisions that need no model — <c>escalate</c> on the third failing report of a paused mandate, one <c>stop</c> on a
/// mandate that is exhausted, expired or revoked — and then starts at most one bounded model run on the oldest unhandled event, in
/// the background, so the poll loop is never held. What is open is read from the bus; only the retry count of an event and the
/// hourly cap live in memory. A run that ends twice without a decision gets a node-written <c>escalate</c>. Never throws to the
/// caller except on cancellation.
/// </summary>
internal sealed partial class SupervisorWake : IDisposable
{
    /// <summary>The runs an event gets before the node escalates it itself.</summary>
    internal const int MaxAttempts = 2;

    /// <summary>
    /// The fixed tool list of a supervisor run: read tools, the read verbs, <c>zyggy decide</c>, <c>zyggy submit</c> only with
    /// <c>--mandate</c>, and the Hub's <c>get_context</c>. No other tool, whatever the event says.
    /// </summary>
    internal static readonly IReadOnlyList<string> Tools =
    [
        "Read",
        "Grep",
        "Glob",
        "Bash(zyggy work)",
        "Bash(zyggy work --waiting)",
        "Bash(zyggy report *)",
        "Bash(zyggy mandate show)",
        "Bash(zyggy decisions)",
        "Bash(zyggy decisions *)",
        "Bash(zyggy decide *)",
        "Bash(zyggy submit *--mandate *)",
        HubTool,
    ];

    /// <summary>The Hub's context tool: part of the fixed list, never given to a run that answers a work machine (founding spec §8).</summary>
    internal const string HubTool = "mcp__hub__get_context";

    /// <summary>The rule that keeps every Hub tool from a run that answers a work machine.</summary>
    internal const string HubDeny = "mcp__hub__*";

    private readonly BusInventory _inventory;
    private readonly DecisionWriter _decisions;
    private readonly IModelRunner _model;
    private readonly IPolicySource _policy;
    private readonly IProcessRunner _processes;
    private readonly RegistrySnapshot _registry;
    private readonly BusIdentity _me;
    private readonly SupervisorWakeOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<SupervisorWake> _log;
    private readonly SupervisorRunBudget _budget;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, int> _attempts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _blocked = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _models = new(StringComparer.Ordinal);
    private Task _run = Task.CompletedTask;
    private bool _running;
    private bool _announced;
    private bool _capLogged;
    private readonly SettingsInUse? _settings;
    private string? _unknownLogged;
    private string? _tableLogged;
    private string? _lastFailure;
    private readonly OwnerNotifier? _owner;
    private readonly string? _decidedFile;
    private readonly TimeSpan _ownerTick;
    private Task _tick = Task.CompletedTask;
    private bool _ticking;
    private bool _stoppedLogged;

    public SupervisorWake(
        BusInventory inventory,
        DecisionWriter decisions,
        IModelRunner model,
        IPolicySource policy,
        IProcessRunner processes,
        RegistrySnapshot registry,
        BusIdentity me,
        SupervisorWakeOptions options,
        TimeProvider clock,
        ILogger<SupervisorWake> log,
        SettingsInUse? settings = null,
        OwnerNotifier? owner = null,
        string? decidedFile = null,
        OwnerMailOptions? mail = null)
    {
        _settings = settings;
        _owner = owner;
        _decidedFile = decidedFile;
        _ownerTick = mail?.Tick ?? new OwnerMailOptions().Tick;
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _decisions = decisions ?? throw new ArgumentNullException(nameof(decisions));
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _me = me ?? throw new ArgumentNullException(nameof(me));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _budget = new SupervisorRunBudget(options.RunsPerHour);
    }

    /// <summary>One pass: the node-written decisions, then at most one supervisor run started in the background.</summary>
    /// <param name="ct">Cancellation token of the caller's pass; a started run is stopped by <see cref="StopAsync"/>, not by this token.</param>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="ct"/> is cancelled.</exception>
    public Task RunAsync(CancellationToken ct)
    {
        // Deliverable 18 ("Mail"): the owner pass also runs between pulls, so an item is named when its window ends and a failed send is tried again.
        if (!_ticking && (_owner is not null || _decidedFile is not null))
        {
            _ticking = true;
            CancellationToken stop = _stop.Token;
            _tick = Task.Run(() => TickAsync(stop), CancellationToken.None);
        }

        return GuardedPassAsync(null, full: true, ct);
    }

    /// <summary>Stops the run in flight, if any, and waits until it has ended (the host calls it after cancelling its loop).</summary>
    public async Task StopAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        Task run;
        do
        {
            run = _run;
            await run.ConfigureAwait(false);
        }
        while (!ReferenceEquals(run, _run));
        await _tick.ConfigureAwait(false);
    }

    /// <summary>Cancels a run in flight without waiting for it.</summary>
    public void Dispose()
    {
        if (!_stop.IsCancellationRequested)
        {
            _stop.Cancel();
        }
    }

    private static int BlockedCalls(string blockedFile)
    {
        try
        {
            return File.Exists(blockedFile) ? File.ReadAllLines(blockedFile).Count(l => l.Length > 0) : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private async Task TickAsync(CancellationToken stop)
    {
        try
        {
            while (true)
            {
                await Task.Delay(_ownerTick, _clock, stop).ConfigureAwait(false);
                await GuardedPassAsync(null, full: false, stop).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // the node is stopping
        }
    }

    private async Task GuardedPassAsync(Action? runEnded, bool full, CancellationToken ct)
    {
        using IDisposable? scope = _log.BeginScope(new Dictionary<string, object>
        {
            ["tenant"] = _me.Tenant.Value,
            ["machine"] = _me.Machine.Value,
            ["phase"] = "supervise",
        });
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            runEnded?.Invoke();
            if (!_announced)
            {
                _announced = true;
                if (Keys().Project is null)
                {
                    LogNoProject();
                }
            }

            if (full)
            {
                await PassAsync(ct).ConfigureAwait(false);
            }

            await OwnerPassAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Deliverable 18 AC-23: the wake never throws to the loop; one Error per streak names the type.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            NoteFailed("pass", ex.GetType().Name);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task PassAsync(CancellationToken ct)
    {
        DateTimeOffset now = _clock.GetUtcNow();
        await CloseMandatesAsync(now, ct).ConfigureAwait(false);

        // Spec 55: the keys in use when the pass starts; a run started now keeps them, whatever the file says later.
        SupervisorKeys keys = Keys();
        IReadOnlyList<SupervisorEvent> all = await _inventory.SupervisorEventsAsync(now, ct).ConfigureAwait(false);

        // Spec 59 slice 10: every report without a mandate is handed to Central's session (ids and names only); no run, no job.
        int arrived = ReportArrivals.Record(ReportArrivals.FileFor(_options.NodeDir, _me.Tenant), all, keys.WorkMachines.Contains, now);
        if (arrived > 0)
        {
            LogArrived(arrived);
        }

        if (OwnerStopped())
        {
            return;
        }

        if (_running || _stop.IsCancellationRequested)
        {
            return;
        }

        IReadOnlyList<SupervisorEvent> events = [.. all.Where(e => !e.Handled && (!IsWorkMachine(e, keys) || keys.OutboundPaths.Count > 0))];

        // An event two runs left without a decision: the node escalates it itself, and no third run starts on it.
        foreach (SupervisorEvent undecided in events.Where(e => Attempts(e) >= MaxAttempts))
        {
            string text = string.Create(
                CultureInfo.InvariantCulture,
                $"supervisor could not decide: {MaxAttempts} runs ended without a decision (blocked calls: {_blocked.GetValueOrDefault(undecided.Id.Value)})");
            await WriteAsync(
                new DecisionRequest(undecided.Id, undecided.Machine, DecisionKind.Escalate, DecisionBasis.Reported, text, "nothing to undo", undecided.Mandate),
                ct).ConfigureAwait(false);
        }

        if (SupervisorStep.Next([.. events.Where(e => Attempts(e) < MaxAttempts)]) is not { } next || ProjectDirectory(keys.Project) is not { } directory)
        {
            return;
        }

        // Spec 50 (AC-11, Q7): the run's model from the table in Central's own project; an invalid table starts no run, logged once.
        // A retry reuses exactly the first attempt's model: the table is read once per event, so an edit between attempts changes nothing.
        SupervisorModel model = _models.TryGetValue(next.Id.Value, out string? first)
            ? new SupervisorModel(first, null)
            : SupervisorModel.For(directory, ModelTable.ReadFileOrNull);
        if (model.Error is { } tableError)
        {
            if (!string.Equals(_tableLogged, tableError, StringComparison.Ordinal))
            {
                _tableLogged = tableError;
                LogTableInvalid(tableError);
            }

            return;
        }

        _tableLogged = null;
        if (!_budget.TryStart(now))
        {
            if (!_capLogged)
            {
                _capLogged = true;
                LogCapReached(_budget.PerHour);
            }

            return;
        }

        _capLogged = false;
        int attempt = Attempts(next) + 1;
        _attempts[next.Id.Value] = attempt;
        _models[next.Id.Value] = model.Model;
        _running = true;
        CancellationToken stop = _stop.Token;
        _run = Task.Run(() => StepAsync(next, directory, attempt, keys, model.Model, stop), CancellationToken.None);
    }

    // Deliverable 18 ("Stop"): while the owner's marker is in the node folder no run starts; the node-written decisions and the mail go on.
    private bool OwnerStopped()
    {
        bool stopped = SupervisionSwitch.IsStopped(_options.NodeDir);
        if (stopped != _stoppedLogged)
        {
            _stoppedLogged = stopped;
            if (stopped)
            {
                LogOwnerStopped();
            }
            else
            {
                LogOwnerStarted();
            }
        }

        return stopped;
    }

    // Deliverable 18 ("The owner's view"): one mail per change of the waiting list, and the decisions handed to the morning brief.
    private async Task OwnerPassAsync(CancellationToken ct)
    {
        SupervisorKeys keys = Keys();
        if (_owner is not null)
        {
            await _owner.RunAsync(await _inventory.WaitingAsync(keys.WorkMachines, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        }

        if (_decidedFile is not null)
        {
            IReadOnlyList<DecisionRecord> decisions = await _inventory.DecisionsAsync(null, null, ct).ConfigureAwait(false);
            DecidedFile.Write(_decidedFile, decisions, keys.WorkMachines.Contains, _clock.GetUtcNow());
        }
    }

    private static bool IsWorkMachine(SupervisorEvent supervisorEvent, SupervisorKeys keys) => keys.WorkMachines.Contains(supervisorEvent.Machine);

    private int Attempts(SupervisorEvent supervisorEvent) => _attempts.GetValueOrDefault(supervisorEvent.Id.Value);

    // The three supervision keys of the five a running node applies (spec 55): from the settings in use, or the options without them.
    private SupervisorKeys Keys()
    {
        if (_settings is null)
        {
            return new SupervisorKeys(_options.Project, _options.WorkMachines, _options.OutboundPaths);
        }

        SettingsValues current = _settings.Current;
        return new SupervisorKeys(current.Project, current.WorkMachines, current.OutboundPaths);
    }

    // What a run that answers a work machine may read (founding spec §8): the outbound folders, and the one question it answers.
    private List<string> ReadRoots(SupervisorEvent supervisorEvent, SupervisorKeys keys)
    {
        List<string> roots = [.. keys.OutboundPaths];
        if (QuestionFile(supervisorEvent) is { } question)
        {
            roots.Add(question);
        }

        return roots;
    }

    // Where a question sits in the bus checkout; the prompt names it, so a run does not have to look for it. Null for a report.
    private string? QuestionFile(SupervisorEvent supervisorEvent) =>
        supervisorEvent.Kind == SupervisorEventKind.Question
            ? BusPaths.Resolve(_options.BusRoot, BusPaths.EnvelopeFile(BusPaths.Questions(_me.Tenant, supervisorEvent.Machine), supervisorEvent.Id))
            : null;

    // The decisions that need no model: escalate on the report that paused a mandate, one stop on a mandate that closed.
    private async Task CloseMandatesAsync(DateTimeOffset now, CancellationToken ct)
    {
        IReadOnlyList<MandateLedger> ledgers = await _inventory.MandatesAsync(ct).ConfigureAwait(false);
        if (ledgers.Count == 0)
        {
            return;
        }

        IReadOnlyList<DecisionRecord> decisions = await _inventory.DecisionsAsync(null, null, ct).ConfigureAwait(false);
        foreach (MandateLedger ledger in ledgers)
        {
            if (ledger.Mandate is not { Mandate: { } fields } mandate)
            {
                continue;
            }

            MandateState state = ledger.StateAt(now);
            if (state == MandateState.Paused)
            {
                IReadOnlyList<MandateReportEntry> reports = SupervisorSummary.PauseReports(ledger);
                MandateReportEntry third = reports[^1];
                if (!decisions.Any(d => d.InReplyTo == third.Id && d.Fields.Kind == DecisionKind.Escalate))
                {
                    await WriteAsync(
                        new DecisionRequest(
                            third.Id,
                            mandate.Header.To,
                            DecisionKind.Escalate,
                            DecisionBasis.Reported,
                            SupervisorSummary.Paused(ledger),
                            "zyggy mandate resume " + ledger.Id.Value,
                            ledger.Id,
                            fields.Project,
                            third.Step,
                            [.. reports.Select(r => r.Id.Value)]),
                        ct).ConfigureAwait(false);
                }
            }

            // An exhausted mandate is closed once its last job has reported, so the summary holds every step.
            bool closed = state is MandateState.Revoked or MandateState.Expired || (state == MandateState.Exhausted && ledger.OpenJobs.Count == 0);
            if (closed && !decisions.Any(d => d.InReplyTo == ledger.Id && d.Fields.Kind == DecisionKind.Stop))
            {
                await WriteAsync(
                    new DecisionRequest(
                        ledger.Id,
                        mandate.Header.To,
                        DecisionKind.Stop,
                        DecisionBasis.Reported,
                        SupervisorSummary.Stop(state, ledger, decisions),
                        "grant a new mandate",
                        ledger.Id,
                        fields.Project),
                    ct).ConfigureAwait(false);
            }
        }
    }

    private async Task WriteAsync(DecisionRequest request, CancellationToken ct)
    {
        string kind = EnvelopeWire.ToWire(request.Kind);
        DecisionTarget? target = await _inventory.FindDecidableAsync(request.InReplyTo, ct).ConfigureAwait(false);
        if (target is null)
        {
            NoteFailed(kind, "nothing on the bus has the id " + request.InReplyTo.Value);
            return;
        }

        AskDecideResult result = await _decisions.DecideAsync(request with { Project = request.Project ?? target.Project }, target.Question, ct).ConfigureAwait(false);
        switch (result)
        {
            case AskDecidePublished published:
                _lastFailure = null;
                LogWritten(kind, request.InReplyTo.Value, published.Id.Value);
                break;
            case AskDecideRefused refused:
                NoteFailed(kind, refused.Field + " " + refused.Detail);
                break;
            case AskDecideFailed failed:
                NoteFailed(kind, failed.Stage + " " + failed.Detail);
                break;
            default:
                NoteFailed(kind, result.GetType().Name);
                break;
        }
    }

    private string? ProjectDirectory(string? project)
    {
        if (project is null)
        {
            return null;
        }

        if (_registry.TryResolve(project, out string? path))
        {
            _unknownLogged = null;
            return path;
        }

        if (!string.Equals(_unknownLogged, project, StringComparison.Ordinal))
        {
            _unknownLogged = project;
            LogUnknownProject(project);
        }

        return null;
    }

    // One bounded run on one event; whatever happens it ends in the run line and the next pass.
    private async Task StepAsync(SupervisorEvent supervisorEvent, string directory, int attempt, SupervisorKeys keys, string? model, CancellationToken ct)
    {
        string id = supervisorEvent.Id.Value;
        var blocked = 0;
        string outcome;
        try
        {
            outcome = await RunModelAsync(supervisorEvent, directory, attempt, keys, model, count => blocked = count, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
#pragma warning disable CA1031 // Deliverable 18 AC-23: a failing run never stops the node; the type only, a message could carry text.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            outcome = "exception " + ex.GetType().Name;
        }

        try
        {
            await GuardedPassAsync(
                () =>
                {
                    _running = false;
                    _blocked[id] = blocked;
                    LogEnded(id, attempt, outcome, blocked);
                },
                full: true,
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // the node is stopping
        }
    }

    private async Task<string> RunModelAsync(SupervisorEvent supervisorEvent, string directory, int attempt, SupervisorKeys keys, string? model, Action<int> blocked, CancellationToken ct)
    {
        string hook = _options.HookExecutable;
        if (!File.Exists(hook) || !await JobRunner.HookRefusesAnEmptyCallAsync(_processes, hook, ct).ConfigureAwait(false))
        {
            // A hook that cannot start would let every tool call through: the run does not start.
            return "hook unavailable";
        }

        NodePolicy policy = await _policy.GetAsync(ct).ConfigureAwait(false);
        string blockedFile = TranscriptPaths.BlockedFor(_options.NodeDir, _me.Tenant, supervisorEvent.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(blockedFile)!);
        File.Delete(blockedFile);
        List<string> hookArgs = ["hook", "pre-tool-use", "--root", directory, "--blocked-file", blockedFile];
        bool workMachine = IsWorkMachine(supervisorEvent, keys);
        IReadOnlyList<string> tools = workMachine ? [.. Tools.Where(t => t != HubTool)] : Tools;
        foreach (string rule in tools)
        {
            hookArgs.AddRange(["--allow", rule]);
        }

        if (workMachine)
        {
            foreach (string root in ReadRoots(supervisorEvent, keys))
            {
                hookArgs.AddRange(["--read-root", root]);
            }
        }

        var request = new ModelRunRequest(SupervisorStep.Prompt(supervisorEvent, QuestionFile(supervisorEvent)), directory, _options.TimeoutOverride ?? TimeSpan.FromMinutes(policy.DefaultTimeoutMinutes))
        {
            MaxTurns = policy.MaxTurns,
            AllowedTools = tools,
            DisallowedTools = workMachine ? [HubDeny] : [],
            MaxBudgetUsd = policy.MaxBudgetUsd,
            Isolation = ModelSessionIsolation.None,
            PreToolUseHook = new ModelHookCommand(hook, hookArgs),

            // Unattended, like a job run: the session hooks do nothing, and `zyggy submit` without --mandate is refused.
            Environment = new Dictionary<string, string>(_options.ModelEnvironment, StringComparer.Ordinal) { ["ZYGGY_HOOKS"] = "off" },
            TranscriptPath = TranscriptPaths.For(_options.NodeDir, _me.Tenant, supervisorEvent.Id),

            // Spec 50 (AC-11): the table's supervisor model, held to it as a job's (a mismatch or a refusal fails the run); null: today's run.
            Model = model,
            RequireModel = model is not null,
        };
        LogStarted(supervisorEvent.Id.Value, supervisorEvent.Kind == SupervisorEventKind.Report ? "report" : "question", attempt);
        ModelRunResult result = await _model.RunAsync(request, ct).ConfigureAwait(false);
        blocked(BlockedCalls(blockedFile));
        return result.Outcome == ModelRunOutcome.Succeeded ? "done" : "failed " + (result.FailureDetail ?? "unknown");
    }

    private void NoteFailed(string stage, string detail)
    {
        string failure = stage + ": " + detail;
        if (!string.Equals(failure, _lastFailure, StringComparison.Ordinal))
        {
            _lastFailure = failure;
            LogFailed(stage, detail);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "supervisor: no run starts, supervision.project is not set")]
    private partial void LogNoProject();

    [LoggerMessage(Level = LogLevel.Warning, Message = "supervisor: no run starts, supervision.project {Project} is not a project of this machine")]
    private partial void LogUnknownProject(string project);

    [LoggerMessage(Level = LogLevel.Error, Message = "supervisor: no run starts, {Error}")]
    private partial void LogTableInvalid(string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "supervisor: stopped by the owner; no run starts until zyggy supervise start")]
    private partial void LogOwnerStopped();

    [LoggerMessage(Level = LogLevel.Information, Message = "supervisor: started again by the owner")]
    private partial void LogOwnerStarted();

    [LoggerMessage(Level = LogLevel.Warning, Message = "supervisor: hourly cap of {Cap} run(s) reached; the next event waits")]
    private partial void LogCapReached(int cap);

    [LoggerMessage(Level = LogLevel.Information, Message = "supervisor run on {Event} ({Kind}) attempt {Attempt} started")]
    private partial void LogStarted(string @event, string kind, int attempt);

    [LoggerMessage(Level = LogLevel.Information, Message = "supervisor run on {Event} attempt {Attempt} ended: {Outcome}, blocked: {Blocked}")]
    private partial void LogEnded(string @event, int attempt, string outcome, int blocked);

    [LoggerMessage(Level = LogLevel.Information, Message = "supervisor: {Count} report(s) without a mandate arrived; Central's session is told at its next turn end, no run starts")]
    private partial void LogArrived(int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "supervisor: {Kind} on {InReplyTo} written as {Decision}")]
    private partial void LogWritten(string kind, string inReplyTo, string decision);

    [LoggerMessage(Level = LogLevel.Error, Message = "supervisor: failed at {Stage}: {Detail}")]
    private partial void LogFailed(string stage, string detail);
}
