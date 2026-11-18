using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Jobs;
using Zyggy.Core.Models;
using Zyggy.Core.Registry;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;
using Zyggy.Node;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Node;

/// <summary>
/// Spec 18 slice 10 (AC-21..AC-24) through the production host as <c>central</c> over a bare repository: after a pickup pass the node
/// starts one supervisor run on the oldest open event, <c>tools/fake-claude</c> makes the scripted tool calls through the real tool
/// guard and runs the real <c>zyggy decide</c> and <c>zyggy submit --mandate</c>; a mandate that pauses or closes gets its decision from
/// the node itself, without a model.
/// </summary>
public sealed partial class SupervisorWakeTests : IAsyncLifetime
{
    private const string Project = "shop";

    private const string Desk = "central-desk";

    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName Central = MachineName.Central;

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(90);

    private static readonly string[] BlockedThenAllowed = ["blocked Write", "allowed Bash"];

    private NodeHostHarness _harness = null!;

    private PickupMemory _memory = null!;

    private ScriptedModel? _model;

    private int _sequence;

    private string ToolLog => _harness.Scratch.File("tool-log.txt");

    public async ValueTask InitializeAsync()
    {
        CancellationToken ct = CancellationToken.None;
        _harness = await NodeHostHarness.CreateAsync(ct);
        _memory = new PickupMemory();
        _harness.Environment = _memory.Env();
        await _harness.ProjectAsync(ct, name: Desk);
        WriteNodeJson(runsPerHour: 20);
        var registry = new RegistryDocument(Acme, HomeLaptop, RegistryOs.Windows, DateTimeOffset.UtcNow.AddMinutes(-1), null, null, [])
        {
            Capabilities = [RegistryDocument.SupervisionCapability],
        };
        await _harness.Bus.SeedRawFileAsync(BusPaths.Registry(Acme, HomeLaptop), registry.Serialize(), "seed: home-laptop registry", ct);
    }

    public async ValueTask DisposeAsync()
    {
        _memory.Dispose();
        await _harness.DisposeAsync();
    }

    // AC-21: a report of a mandate job starts one run; the run's decision and its job land on the bus.
    [Fact]
    public async Task Wake_ReportOfMandateJob_OneRunDecidesAndSubmits()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        EnvelopeId mandate = await SeedMandateAsync(ct);
        EnvelopeId report = await SeedReportAsync(mandate, ct);
        Script((_, id) => new Step([Decide(id, mandate, "accept"), Submit(mandate, "slice 2")]));

        // Act
        int exit = await _harness.RunUntilAsync(
            async () => (await DecisionsAsync(ct)).Count == 1 && (await FilesOnMainAsync(BusPaths.Jobs(Acme, HomeLaptop), ct)).Count == 1,
            Limit,
            ct,
            Services);

        // Assert
        exit.Should().Be(0, Log());
        Model decision = (await DecisionsAsync(ct)).Single();
        Model job = await ParseAsync((await FilesOnMainAsync(BusPaths.Jobs(Acme, HomeLaptop), ct)).Single(), ct);
        new
        {
            decision.Decision!.Kind,
            decision.Decision.Basis,
            decision.Header.InReplyTo,
            decision.Header.To,
            JobMandate = job.Job!.Mandate,
            JobStep = job.Job.Step,
            Runs = _model!.Calls,
        }.Should().BeEquivalentTo(new
        {
            Kind = DecisionKind.Accept,
            Basis = DecisionBasis.Verified,
            InReplyTo = (EnvelopeId?)report,
            To = HomeLaptop,
            JobMandate = (EnvelopeId?)mandate,
            JobStep = "slice 2",
            Runs = 1,
        }, Log());
    }

    // AC-21: a second event waits until the first run ended; the oldest event goes first.
    [Fact]
    public async Task Wake_TwoEvents_OldestFirst_SecondRunStartsAfterTheFirstEnded()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        EnvelopeId mandate = await SeedMandateAsync(ct);
        EnvelopeId older = await SeedReportAsync(mandate, ct, age: TimeSpan.FromMinutes(10));
        EnvelopeId newer = await SeedReportAsync(mandate, ct, age: TimeSpan.FromMinutes(5));
        Script((call, id) => new Step([Decide(id, mandate, "accept")], DelayMs: call == 0 ? 2000 : null));

        // Act
        int exit = await _harness.RunUntilAsync(async () => (await DecisionsAsync(ct)).Count == 2 && _model!.Runs.Count == 2, Limit, ct, Services);

        // Assert
        exit.Should().Be(0, Log());
        IReadOnlyList<Run> runs = _model!.Runs;
        new { First = runs[0].Event, Second = runs[1].Event, Overlap = runs[1].Started < runs[0].Ended }
            .Should().BeEquivalentTo(new { First = older.Value, Second = newer.Value, Overlap = false }, Log());
    }

    // AC-21: no more than runsPerHour runs start.
    [Fact]
    public async Task Wake_RunsPerHourReached_NextEventNotStarted()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        WriteNodeJson(runsPerHour: 1);
        EnvelopeId mandate = await SeedMandateAsync(ct);
        EnvelopeId older = await SeedReportAsync(mandate, ct, age: TimeSpan.FromMinutes(10));
        await SeedReportAsync(mandate, ct, age: TimeSpan.FromMinutes(5));
        Script((_, id) => new Step([Decide(id, mandate, "accept")]));

        // Act
        int exit = await _harness.RunUntilAsync(
            async () => (await DecisionsAsync(ct)).Count == 1 && _harness.LogLines.Any(l => l.Contains("hourly cap of 1 run(s) reached", StringComparison.Ordinal)),
            Limit,
            ct,
            Services);

        // Assert
        exit.Should().Be(0, Log());
        new { Runs = _model!.Calls, Decided = (await DecisionsAsync(ct)).Select(d => d.Header.InReplyTo!.Value).ToArray() }
            .Should().BeEquivalentTo(new { Runs = 1, Decided = new[] { older.Value } }, Log());
    }

    // AC-22: a tool outside the fixed list is blocked by the guard and counted in the run's record.
    [Fact]
    public async Task Wake_ToolOutsideTheFixedList_BlockedAndCounted()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        EnvelopeId mandate = await SeedMandateAsync(ct);
        EnvelopeId report = await SeedReportAsync(mandate, ct);
        Script((_, id) => new Step([Call("Write", new JsonObject { ["file_path"] = "note.txt", ["content"] = "x" }), Decide(id, mandate, "accept")]));

        // Act
        int exit = await _harness.RunUntilAsync(
            async () => (await DecisionsAsync(ct)).Count == 1 && _harness.LogLines.Any(l => l.Contains("blocked: 1", StringComparison.Ordinal)),
            Limit,
            ct,
            Services);

        // Assert
        exit.Should().Be(0, Log());
        new
        {
            Tools = File.ReadAllLines(ToolLog),
            Blocked = File.ReadAllLines(TranscriptPaths.BlockedFor(_harness.NodeDir, Acme, report)).Length,
            Written = File.Exists(Path.Combine(_harness.ProjectsRoot, Desk, "note.txt")),
        }.Should().BeEquivalentTo(new { Tools = BlockedThenAllowed, Blocked = 1, Written = false }, Log());
    }

    // AC-22: a run that ends twice without a decision leaves a node-written escalate in reply to the event.
    [Fact]
    public async Task Wake_TwoRunsWithoutADecision_NodeWritesEscalateOnTheEvent()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        EnvelopeId mandate = await SeedMandateAsync(ct);
        EnvelopeId report = await SeedReportAsync(mandate, ct);
        Script((_, _) => new Step([Call("Write", new JsonObject { ["file_path"] = "note.txt", ["content"] = "x" })]));

        // Act
        int exit = await _harness.RunUntilAsync(async () => (await DecisionsAsync(ct)).Count == 1, Limit, ct, Services);

        // Assert: the escalate names what the guard blocked in the last run
        exit.Should().Be(0, Log());
        Model decision = (await DecisionsAsync(ct)).Single();
        new { decision.Decision!.Kind, decision.Decision.Basis, decision.Header.InReplyTo, Text = decision.BodyText, Runs = _model!.Calls }
            .Should().BeEquivalentTo(new
            {
                Kind = DecisionKind.Escalate,
                Basis = DecisionBasis.Reported,
                InReplyTo = (EnvelopeId?)report,
                Text = "supervisor could not decide: 2 runs ended without a decision (blocked calls: 1)\n",
                Runs = 2,
            }, Log());
    }

    // AC-23: with a failing supervisor run — the model exits non-zero, or the runner itself throws — the host stays up, /health
    // answers and the next event is handled.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Wake_RunFails_HostStaysUp_HealthAnswers_NextEventHandled(bool throws)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        EnvelopeId mandate = await SeedMandateAsync(ct);
        EnvelopeId failing = await SeedReportAsync(mandate, ct, age: TimeSpan.FromMinutes(10));
        EnvelopeId next = await SeedReportAsync(mandate, ct, age: TimeSpan.FromMinutes(5));
        Script((_, id) => id == failing.Value ? new Step([], Scenario: "error", Exit: 1, Throws: throws) : new Step([Decide(id, mandate, "accept")]));
        using IHost host = _harness.Build(Services);
        await host.StartAsync(ct);
        HealthServer server = host.Services.GetRequiredService<HealthServer>();
        await NodeHostHarness.WaitUntilAsync(() => Task.FromResult(server.IsListening && _model!.Calls >= 1), Limit, ct);

        // Act
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.BoundPort}/") };
        using HttpResponseMessage health = await client.GetAsync(new Uri("/health", UriKind.Relative), ct);
        await NodeHostHarness.WaitUntilAsync(async () => (await DecisionsAsync(ct)).Count == 2, Limit, ct);
        await host.StopAsync(ct);

        // Assert
        Dictionary<string, DecisionKind> kinds = (await DecisionsAsync(ct)).ToDictionary(d => d.Header.InReplyTo!.Value, d => d.Decision!.Kind);
        new { Health = health.StatusCode, Failing = kinds[failing.Value], Next = kinds[next.Value] }
            .Should().BeEquivalentTo(new { Health = HttpStatusCode.OK, Failing = DecisionKind.Escalate, Next = DecisionKind.Accept }, Log());
    }

    // AC-24: a pause leaves a node-written escalate in reply to the third failing report; no model runs.
    [Fact]
    public async Task Wake_ThreeFailingReports_NodeWritesEscalateOnTheThird_NoRun()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        EnvelopeId mandate = await SeedMandateAsync(ct, maxJobs: 10);
        await SeedReportAsync(mandate, ct, ReportStatus.Failed, age: TimeSpan.FromMinutes(15));
        await SeedReportAsync(mandate, ct, ReportStatus.Failed, age: TimeSpan.FromMinutes(10));
        EnvelopeId third = await SeedReportAsync(mandate, ct, ReportStatus.Failed, age: TimeSpan.FromMinutes(5));
        Script((_, _) => new Step([]));

        // Act
        int exit = await _harness.RunUntilAsync(async () => (await DecisionsAsync(ct)).Count >= 1 && await QuietForAsync(TimeSpan.FromSeconds(4)), Limit, ct, Services);

        // Assert
        exit.Should().Be(0, Log());
        Model decision = (await DecisionsAsync(ct)).Should().ContainSingle(Log()).Subject;
        new { decision.Decision!.Kind, decision.Header.InReplyTo, decision.Decision.Mandate, decision.Header.To, Runs = _model!.Calls }
            .Should().BeEquivalentTo(new { Kind = DecisionKind.Escalate, InReplyTo = (EnvelopeId?)third, Mandate = (EnvelopeId?)mandate, To = HomeLaptop, Runs = 0 }, Log());
    }

    // AC-24: a mandate that is exhausted, expired or revoked gets exactly one node-written stop in reply to the mandate id, also after a restart.
    [Theory]
    [InlineData("exhausted")]
    [InlineData("expired")]
    [InlineData("revoked")]
    public async Task Wake_ClosedMandate_ExactlyOneStopOnTheMandate_AlsoAfterARestart(string state)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        EnvelopeId mandate = await SeedMandateAsync(ct, maxJobs: 1, expired: state == "expired", revoked: state == "revoked");
        if (state == "exhausted")
        {
            await SeedReportAsync(mandate, ct);
        }

        Script((_, _) => new Step([]));
        int first = await _harness.RunUntilAsync(async () => (await DecisionsAsync(ct)).Count >= 1, Limit, ct, Services);
        first.Should().Be(0, Log());

        // Act: a second start on the same bus
        _quietSince = null;
        int exit = await _harness.RunUntilAsync(() => QuietForAsync(TimeSpan.FromSeconds(8)), Limit, ct, Services);

        // Assert
        exit.Should().Be(0, Log());
        Model decision = (await DecisionsAsync(ct)).Should().ContainSingle(Log()).Subject;
        new { decision.Decision!.Kind, decision.Header.InReplyTo, decision.Decision.Mandate, decision.Header.To, Named = decision.BodyText.StartsWith("mandate " + state, StringComparison.Ordinal), Runs = _model!.Calls }
            .Should().BeEquivalentTo(new { Kind = DecisionKind.Stop, InReplyTo = (EnvelopeId?)mandate, Mandate = (EnvelopeId?)mandate, To = HomeLaptop, Named = true, Runs = 0 }, decision.BodyText + Log());
    }

    private DateTimeOffset? _quietSince;

    /// <summary>True once <paramref name="time"/> has passed since the first call (the host had its start-up pass and several polls).</summary>
    private Task<bool> QuietForAsync(TimeSpan time)
    {
        _quietSince ??= DateTimeOffset.UtcNow;
        return Task.FromResult(DateTimeOffset.UtcNow - _quietSince > time);
    }

    private string Log() => string.Join('\n', _harness.LogLines);

    private void WriteNodeJson(int runsPerHour)
    {
        string path = _harness.WriteNodeJson(machine: Central);
        JsonNode json = JsonNode.Parse(File.ReadAllText(path))!;
        json["supervision"] = new JsonObject { ["project"] = Desk, ["runsPerHour"] = runsPerHour };
        File.WriteAllText(path, json.ToJsonString());
    }

    /// <summary>Wraps the production model runner: every supervisor run gets its script for <c>tools/fake-claude</c>.</summary>
    private void Script(Func<int, string, Step> script) => _script = script;

    private Func<int, string, Step> _script = (_, _) => new Step([]);

    private void Services(IServiceCollection services)
    {
        ServiceDescriptor production = services.Last(d => d.ServiceType == typeof(IModelRunner));
        _model ??= new ScriptedModel();
        ScriptedModel model = _model;
        services.AddSingleton<IModelRunner>(sp =>
        {
            model.Inner = (IModelRunner)production.ImplementationFactory!(sp);
            model.Script = (call, id) => Environment(_script(call, id));
            return model;
        });
    }

    private Dictionary<string, string> Environment(Step step)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ZYGGY_FAKE_CLAUDE_SCENARIO"] = step.Scenario,
            ["ZYGGY_FAKE_CLAUDE_ZYGGY"] = ZyggyCli.ExecutablePath,
            ["ZYGGY_FAKE_CLAUDE_TOOL_LOG"] = ToolLog,
            ["ZYGGY_NODE_JSON"] = _harness.NodeJsonPath,
            ["ZYGGY_SECRET_PATTERNS"] = PickupMemory.PatternsPath,
        };
        foreach ((string name, string? value) in _harness.Bus.GitEnvironment)
        {
            if (value is not null)
            {
                environment[name] = value;
            }
        }

        if (step.ToolCalls.Count > 0)
        {
            environment["ZYGGY_FAKE_CLAUDE_TOOL_CALLS"] = string.Join('\n', step.ToolCalls);
        }

        if (step.DelayMs is { } delay)
        {
            environment["ZYGGY_FAKE_CLAUDE_DELAY_MS"] = delay.ToString(CultureInfo.InvariantCulture);
        }

        if (step.Throws)
        {
            environment[ScriptedModel.ThrowKey] = "1";
        }

        if (step.Exit is { } exit)
        {
            environment["ZYGGY_FAKE_CLAUDE_EXIT"] = exit.ToString(CultureInfo.InvariantCulture);
        }

        return environment;
    }

    private static string Call(string tool, JsonObject input, string[]? run = null)
    {
        var call = new JsonObject { ["tool_name"] = tool, ["tool_input"] = input };
        if (run is not null)
        {
            call["run"] = new JsonArray([.. run.Select(a => (JsonNode)a)]);
        }

        return call.ToJsonString();
    }

    private static string Zyggy(params string[] args) =>
        Call("Bash", new JsonObject { ["command"] = "zyggy " + string.Join(' ', args.Select(a => a.Contains(' ', StringComparison.Ordinal) ? "\"" + a + "\"" : a)) }, args);

    private static string Decide(string inReplyTo, EnvelopeId mandate, string kind) =>
        Zyggy("decide", "--reply-to", inReplyTo, "--kind", kind, "--basis", "verified", "--mandate", mandate.Value, "--step", "slice 1", "--evidence", inReplyTo, "--undo", "nothing to undo", "Every criterion of the step is met.");

    private static string Submit(EnvelopeId mandate, string step) =>
        Zyggy("submit", "--to", "home-laptop", "--mandate", mandate.Value, "--kind", "build", "--step", step);

    private async Task<EnvelopeId> SeedMandateAsync(CancellationToken ct, int maxJobs = 5, bool expired = false, bool revoked = false)
    {
        DateTimeOffset granted = DateTimeOffset.UtcNow.AddDays(expired ? -6 : 0).AddHours(-1);
        EnvelopeId id = EnvelopeId.New(granted);
        var fields = new MandateFields(
            Project,
            "Build the shop",
            ["Read", "Edit"],
            maxJobs,
            expired ? granted.AddDays(1) : granted.AddDays(3),
            Spec: "_specs/12-shop.md",
            SpecCommit: "0123456789abcdef0123456789abcdef01234567");
        Model mandate = Model.CreateMandate(new EnvelopeHeader(Acme, id, Central, HomeLaptop, granted), fields);
        EnvelopeResult signed = await new EnvelopeSigner(_harness.Store).SignAsync(mandate, TestKeys.Acme1, ct);
        signed.IsAccepted.Should().BeTrue(signed.Rejection?.ToString());
        string folder = revoked ? BusPaths.RevokedMandates(Acme) : BusPaths.Mandates(Acme);
        await _harness.Bus.SeedRawFileAsync(BusPaths.EnvelopeFile(folder, id), EnvelopeWriter.Serialize(signed.Envelope!), "acme mandate " + id.Value + " central→home-laptop", ct);
        return id;
    }

    /// <summary>A report of <c>home-laptop</c> to Central on a job under <paramref name="mandate"/>, written <paramref name="age"/> ago.</summary>
    private async Task<EnvelopeId> SeedReportAsync(EnvelopeId mandate, CancellationToken ct, ReportStatus status = ReportStatus.Done, TimeSpan? age = null)
    {
        DateTimeOffset created = DateTimeOffset.UtcNow - (age ?? TimeSpan.FromMinutes(5)) + TimeSpan.FromMilliseconds(++_sequence);
        EnvelopeId job = EnvelopeId.New(created.AddMinutes(-2));
        SeededJob report = await _harness.Bus.SeedSignedReportAsync(
            HomeLaptop,
            Central,
            job,
            TestKeys.Acme1,
            _harness.Store,
            status == ReportStatus.Done ? "The step is built.\n" : "claude_error: exit 1\n",
            created: created,
            fields: new ReportFields(status, status == ReportStatus.Done ? null : "claude_error", Mandate: mandate, Step: "slice 1"),
            ct: ct);
        return report.Id;
    }

    private async Task<IReadOnlyList<Model>> DecisionsAsync(CancellationToken ct)
    {
        var decisions = new List<Model>();
        foreach (string path in await FilesOnMainAsync(BusPaths.Decisions(Acme, Central), ct))
        {
            decisions.Add(await ParseAsync(path, ct));
        }

        return decisions;
    }

    private async Task<Model> ParseAsync(string path, CancellationToken ct)
    {
        EnvelopeResult parsed = await new EnvelopeSigner(_harness.Store).ParseAndVerifyAsync(await _harness.Bus.ShowBytesAsync(path, ct), Acme, ct);
        parsed.IsAccepted.Should().BeTrue(parsed.Rejection?.ToString());
        return parsed.Envelope!;
    }

    private async Task<IReadOnlyList<string>> FilesOnMainAsync(string folder, CancellationToken ct)
    {
        string listing = await _harness.Bus.RunGitAsync(_harness.Bus.BareDir, ["ls-tree", "-r", "--name-only", "main", "--", folder], ct);
        return [.. listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.EndsWith(".md", StringComparison.Ordinal) && string.Equals(p[..p.LastIndexOf('/')], folder, StringComparison.Ordinal))];
    }

    [GeneratedRegex("on event ([0-9A-Z]{26})", RegexOptions.CultureInvariant)]
    private static partial Regex EventId();

    /// <summary>What <c>tools/fake-claude</c> does in one supervisor run.</summary>
    private sealed record Step(IReadOnlyList<string> ToolCalls, int? DelayMs = null, string Scenario = "done", int? Exit = null, bool Throws = false);

    private sealed record Run(string Event, DateTimeOffset Started, DateTimeOffset Ended);

    /// <summary>The production runner with a per-run environment for the fake; runs that are not a supervisor step pass through.</summary>
    private sealed class ScriptedModel : IModelRunner
    {
        /// <summary>A script entry that makes the runner itself throw instead of starting the fake.</summary>
        public const string ThrowKey = "TEST_RUNNER_THROWS";

        private readonly List<Run> _runs = [];
        private int _calls;

        public IModelRunner Inner { get; set; } = null!;

        public Func<int, string, Dictionary<string, string>> Script { get; set; } = null!;

        public int Calls => Volatile.Read(ref _calls);

        public IReadOnlyList<Run> Runs
        {
            get
            {
                lock (_runs)
                {
                    return [.. _runs];
                }
            }
        }

        public Task<bool?> CheckAuthAsync(CancellationToken cancellationToken) => Inner.CheckAuthAsync(cancellationToken);

        public async Task<ModelRunResult> RunAsync(ModelRunRequest request, CancellationToken cancellationToken)
        {
            Match match = EventId().Match(request.Prompt);
            if (!request.Prompt.StartsWith("You are Central's supervisor.", StringComparison.Ordinal) || !match.Success)
            {
                return await Inner.RunAsync(request, cancellationToken);
            }

            int call = Interlocked.Increment(ref _calls) - 1;
            string id = match.Groups[1].Value;
            var environment = new Dictionary<string, string>(request.Environment, StringComparer.Ordinal);
            foreach ((string name, string value) in Script(call, id))
            {
                environment[name] = value;
            }

            if (environment.ContainsKey(ThrowKey))
            {
                throw new InvalidOperationException("the runner broke");
            }

            DateTimeOffset started = DateTimeOffset.UtcNow;
            try
            {
                return await Inner.RunAsync(request with { Environment = environment }, cancellationToken);
            }
            finally
            {
                lock (_runs)
                {
                    _runs.Add(new Run(id, started, DateTimeOffset.UtcNow));
                }
            }
        }
    }
}
