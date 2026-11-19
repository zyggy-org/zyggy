using System.Text;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;
using Zyggy.Core.Envelope;
using Zyggy.Core.Models;
using Zyggy.Core.Registry;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;
using Step = Zyggy.Integration.Infrastructure.SupervisorStepScript;

namespace Zyggy.Integration.Node;

/// <summary>
/// Spec 55 slice 6 through the production host as <c>central</c> and the real <c>zyggy settings</c> verb, without a restart: the owner's
/// root with its exclude, then the supervision project, reach the registry (the instance project, never the home folder in any commit)
/// and the next question's supervisor run (AC-13); a supervisor run that started before a change of the outbound folders keeps the read
/// root it started with, and the next run uses the new one (AC-14).
/// </summary>
public sealed class SettingsAppliedTests : IAsyncLifetime
{
    private const string Srv = "srv";

    private const string Instance = "instance";

    private const string Home = "home";

    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName Central = MachineName.Central;

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(120);

    private static readonly TimeSpan CheckPeriod = TimeSpan.FromMilliseconds(250);

    private static readonly string[] OnlyTheInstance = [Instance];

    private static readonly int[] BothOk = [0, 0];

    private NodeHostHarness _harness = null!;

    private PickupMemory _memory = null!;

    private SupervisorScript _model = null!;

    private string Parent => Path.Combine(_harness.ProjectsRoot, Srv);

    private string InstancePath => Path.Combine(Parent, Instance);

    private string Business => Path.Combine(_memory.Root, "acme", "alice", "business");

    private string Clients => Path.Combine(Business, "clients");

    public async ValueTask InitializeAsync()
    {
        CancellationToken ct = CancellationToken.None;
        _harness = await NodeHostHarness.CreateAsync(ct);
        _model = new SupervisorScript(_harness);
        _memory = new PickupMemory();
        _harness.Environment = _memory.Env();
        Directory.CreateDirectory(Clients);
        await _harness.ProjectAsync(ct, name: Instance, group: Srv);

        // The service user's home: Claude Code's own .claude/ and a clone below it, both of which a scan would otherwise publish.
        Directory.CreateDirectory(Path.Combine(Parent, Home, ".claude"));
        Directory.CreateDirectory(Path.Combine(Parent, Home, "zyggy-core", ".claude"));
    }

    public async ValueTask DisposeAsync()
    {
        _memory.Dispose();
        await _harness.DisposeAsync();
    }

    // AC-13: no project published; the verb adds the parent with the home folder excluded, then names the instance project: the registry
    // then holds the instance project, and no version of the registry file on the bus ever held the home folder.
    [Fact]
    public async Task Verb_WhileRunning_RegistryGetsTheInstance_NeverHome()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        _harness.WriteNodeJson(machine: Central, devRoots: []);
        var exits = new List<int>();
        var stage = 0;

        // Act
        int exit = await _harness.RunUntilAsync(
            async () =>
            {
                RegistryDocument? registry = await _harness.RegistryOnMainAsync(Central, ct);
                if (stage == 0 && registry is not null)
                {
                    stage = 1;
                    exits.Add(await VerbAsync(ct, "add", "devRoots", Parent, "--exclude", Home));
                    exits.Add(await VerbAsync(ct, "set", "supervision.project", Instance));
                    return false;
                }

                return stage == 1 && registry is not null && registry.Projects.Any(p => p.Name == Instance);
            },
            Limit,
            ct,
            Register);

        // Assert
        exit.Should().Be(0, Log());
        new
        {
            Exits = exits,
            Published = (await _harness.RegistryOnMainAsync(Central, ct))!.Projects.Select(p => p.Name).ToArray(),
            EverHome = (await RegistryVersionsAsync(ct)).Any(r => r.Projects.Any(p => p.Name == Home || p.Name.Contains(Home + "/", StringComparison.Ordinal))),
        }.Should().BeEquivalentTo(new { Exits = BothOk, Published = OnlyTheInstance, EverHome = false }, Log());
    }

    // AC-13: after the two verb calls, the next question starts one supervisor run in the instance folder, without a restart.
    [Fact]
    public async Task Verb_WhileRunning_NextQuestionStartsASupervisorRunInTheInstance()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        _harness.WriteNodeJson(machine: Central, devRoots: []);
        EnvelopeId? question = null;
        var stage = 0;
        _model.Script = (_, id) => new Step(
        [
            SupervisorScript.Zyggy("decide", "--reply-to", id, "--kind", "answer", "--basis", "reported", "--undo", "nothing to undo", "Anna is the contact."),
        ]);

        // Act
        int exit = await _harness.RunUntilAsync(
            async () =>
            {
                if (stage == 0 && await _harness.RegistryOnMainAsync(Central, ct) is not null)
                {
                    stage = 1;
                    await VerbAsync(ct, "add", "devRoots", Parent, "--exclude", Home);
                    await VerbAsync(ct, "set", "supervision.project", Instance);
                }
                else if (stage == 1 && (await _harness.RegistryOnMainAsync(Central, ct))!.Projects.Any(p => p.Name == Instance) && Applied("supervision.project"))
                {
                    stage = 2;
                    question = await SeedQuestionAsync(ct);
                }

                return stage == 2 && (await DecisionsAsync(ct)).Count == 1;
            },
            Limit,
            ct,
            Register);

        // Assert
        exit.Should().Be(0, Log());
        new
        {
            Runs = _model.Calls,
            Folder = Normal(_model.Runs.Single().Request.WorkingDirectory),
            (await DecisionsAsync(ct)).Single().Header.InReplyTo,
        }.Should().BeEquivalentTo(new { Runs = 1, Folder = Normal(InstancePath), InReplyTo = question }, Log());
    }

    // AC-14: home-laptop is a work machine; the first run is in flight when the verb adds an outbound folder. That run keeps the read
    // root it started with; the run for the next question has the new folder too. The verb runs once the first run's decision is on
    // the bus (the run then holds 8 s before it ends): run at the run's start, the verb's replace of node.json could refuse the
    // run's own `zyggy decide` its read of that file on Windows, and the question was answered by a second run. That the change landed
    // while the first run was in flight is asserted, not assumed.
    [Fact]
    public async Task OutboundAdded_DuringARun_ThatRunKeepsItsReadRoot_NextRunUsesTheNewOne()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string path = _harness.WriteNodeJson(machine: Central, devRoots: [Parent]);
        JsonNode json = JsonNode.Parse(await File.ReadAllTextAsync(path, ct))!;
        json["discovery"] = new JsonObject { ["exclude"] = new JsonArray(Home) };
        json["supervision"] = new JsonObject
        {
            ["project"] = Instance,
            ["workMachines"] = new JsonArray(HomeLaptop.Value),
            ["outboundPaths"] = new JsonArray(Business.Replace('\\', '/')),
        };
        await File.WriteAllTextAsync(path, json.ToJsonString(), ct);
        EnvelopeId first = await SeedQuestionAsync(ct);
        EnvelopeId? second = null;
        var verbExit = -1;
        DateTimeOffset verbDone = DateTimeOffset.MaxValue;
        var stage = 0;
        _model.Script = (call, id) => new Step(
            [SupervisorScript.Zyggy("decide", "--reply-to", id, "--kind", "answer", "--basis", "reported", "--undo", "nothing to undo", "Anna is the contact.")],
            DelayMs: call == 0 ? 8000 : null);

        // Act
        int exit = await _harness.RunUntilAsync(
            async () =>
            {
                if (stage == 0 && _model.Calls == 1 && (await DecisionsAsync(ct)).Count == 1)
                {
                    stage = 1;
                    verbExit = await VerbAsync(ct, "add", "supervision.outboundPaths", Clients);
                    verbDone = DateTimeOffset.UtcNow;
                }
                else if (stage == 1 && Applied("supervision.outboundPaths"))
                {
                    stage = 2;
                    second = await SeedQuestionAsync(ct);
                }

                return stage == 2 && (await DecisionsAsync(ct)).Count == 2;
            },
            Limit,
            ct,
            Register);

        // Assert
        exit.Should().Be(0, Log());
        SupervisorRunRecord firstRun = _model.Runs.Single(r => r.Event == first.Value);
        new
        {
            verbExit,
            ChangedDuringTheFirstRun = verbDone < firstRun.Ended,
            First = MemoryRoots(firstRun.Request),
            Next = MemoryRoots(_model.Runs.Single(r => r.Event == second!.Value).Request),
        }.Should().BeEquivalentTo(new
        {
            verbExit = 0,
            ChangedDuringTheFirstRun = true,
            First = new[] { Normal(Business) },
            Next = new[] { Normal(Business), Normal(Clients) },
        }, Log());
    }

    private static string Normal(string path) => Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/');

    private string[] MemoryRoots(ModelRunRequest request)
    {
        IReadOnlyList<string> args = request.PreToolUseHook!.Args;
        string memory = Normal(_memory.Root);
        return [.. Enumerable.Range(0, args.Count - 1)
            .Where(i => args[i] == "--read-root")
            .Select(i => Normal(args[i + 1]))
            .Where(p => p.StartsWith(memory, StringComparison.Ordinal))];
    }

    private void Register(IServiceCollection services)
    {
        _model.Register(services);
        services.Configure<SettingsInUseOptions>(o => o.CheckPeriodOverride = CheckPeriod);
    }

    private bool Applied(string key) =>
        _harness.LogLines.Any(l => l.Contains("settings: applied ", StringComparison.Ordinal) && l.Contains(key, StringComparison.Ordinal));

    private string Log() => string.Join('\n', _harness.LogLines);

    /// <summary>The real verb in process, attended (no <c>ZYGGY_HOOKS</c>), against the node's own <c>node.json</c>.</summary>
    private async Task<int> VerbAsync(CancellationToken ct, params string[] args)
    {
        var (exit, console) = await ZyggyCliInProcess.RunAsync(["settings", .. args, "--config", _harness.NodeJsonPath], _memory.Env(), ct: ct);
        exit.Should().Be(0, console.Stderr);
        return exit;
    }

    private async Task<EnvelopeId> SeedQuestionAsync(CancellationToken ct)
    {
        DateTimeOffset created = DateTimeOffset.UtcNow.AddMinutes(-5);
        EnvelopeId id = EnvelopeId.New(created);
        Model question = Model.CreateQuestion(
            new EnvelopeHeader(Acme, id, HomeLaptop, Central, created),
            new QuestionFields("calizr", "wait for the owner", ["Anna", "Bram"], Session: "a1b2c3d4"),
            Encoding.UTF8.GetBytes("Who is the contact for the booking pool?\n"));
        EnvelopeResult signed = await new EnvelopeSigner(_harness.Store).SignAsync(question, TestKeys.Acme1, ct);
        signed.IsAccepted.Should().BeTrue(signed.Rejection?.ToString());
        string path = BusPaths.EnvelopeFile(BusPaths.Questions(Acme, HomeLaptop), id);
        await _harness.Bus.SeedRawFileAsync(path, EnvelopeWriter.Serialize(signed.Envelope!), $"acme question {id.Value} home-laptop→central", ct);
        return id;
    }

    /// <summary>Every version of Central's registry file in the history of <c>main</c>.</summary>
    private async Task<IReadOnlyList<RegistryDocument>> RegistryVersionsAsync(CancellationToken ct)
    {
        string path = BusPaths.Registry(Acme, Central);
        string log = await _harness.Bus.RunGitAsync(_harness.Bus.BareDir, ["log", "--format=%H", "main", "--", path], ct);
        var versions = new List<RegistryDocument>();
        foreach (string sha in log.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string text = await _harness.Bus.RunGitAsync(_harness.Bus.BareDir, ["show", sha + ":" + path], ct);
            if (RegistryDocument.Parse(Encoding.UTF8.GetBytes(text)).Document is { } document)
            {
                versions.Add(document);
            }
        }

        return versions;
    }

    private async Task<IReadOnlyList<Model>> DecisionsAsync(CancellationToken ct)
    {
        string folder = BusPaths.Decisions(Acme, Central);
        string listing = await _harness.Bus.RunGitAsync(_harness.Bus.BareDir, ["ls-tree", "-r", "--name-only", "main", "--", folder], ct);
        var decisions = new List<Model>();
        foreach (string path in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.EndsWith(".md", StringComparison.Ordinal) && string.Equals(p[..p.LastIndexOf('/')], folder, StringComparison.Ordinal)))
        {
            EnvelopeResult parsed = await new EnvelopeSigner(_harness.Store).ParseAndVerifyAsync(await _harness.Bus.ShowBytesAsync(path, ct), Acme, ct);
            parsed.IsAccepted.Should().BeTrue(parsed.Rejection?.ToString());
            decisions.Add(parsed.Envelope!);
        }

        return decisions;
    }
}
