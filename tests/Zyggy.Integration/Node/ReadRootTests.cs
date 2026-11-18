using System.Text;
using System.Text.Json.Nodes;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Jobs;
using Zyggy.Core.Models;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;
using Step = Zyggy.Integration.Infrastructure.SupervisorStepScript;

namespace Zyggy.Integration.Node;

/// <summary>
/// Spec 18 slice 11 (AC-25) through the production host as <c>central</c>: a question from a machine in
/// <c>supervision.workMachines</c> is answered by a supervisor run whose tool guard carries a read root — the outbound folders and
/// the one question — and that has no Hub tool; a read outside the root is refused and counted, and a canary line outside the root
/// never reaches the decision. A question from another machine runs as before.
/// </summary>
public sealed class ReadRootTests : IAsyncLifetime
{
    private const string Desk = "central-desk";

    private const string Canary = "CANARY-7391 the cellar key is under the third stone";

    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName Central = MachineName.Central;

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly MachineName WorkLaptop = MachineName.Parse("work-laptop");

    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(90);

    private static readonly string[] OutsideRefusedInsideAllowed = ["blocked Read", "blocked Bash", "allowed Read", "allowed Read", "allowed Bash"];

    private static readonly string[] HubDenied = ["mcp__hub__*"];

    private static readonly bool[] BothByTheReadRoot = [true, true];

    private NodeHostHarness _harness = null!;

    private PickupMemory _memory = null!;

    private SupervisorScript _model = null!;

    private string Outbound => _harness.Scratch.File("outbound");

    private string OutboundNote => Path.Combine(Outbound, "people.md").Replace('\\', '/');

    private string CanaryFile => Path.Combine(_harness.ProjectsRoot, Desk, "private-notes.md").Replace('\\', '/');

    public async ValueTask InitializeAsync()
    {
        CancellationToken ct = CancellationToken.None;
        _harness = await NodeHostHarness.CreateAsync(ct);
        _model = new SupervisorScript(_harness);
        _memory = new PickupMemory();
        _harness.Environment = _memory.Env();
        await _harness.ProjectAsync(ct, name: Desk);
        Directory.CreateDirectory(Outbound);
        await File.WriteAllTextAsync(OutboundNote, "- Anna is the contact for the booking pool\n", ct);
        await File.WriteAllTextAsync(CanaryFile, Canary + "\n", ct);
        string path = _harness.WriteNodeJson(machine: Central);
        JsonNode json = JsonNode.Parse(await File.ReadAllTextAsync(path, ct))!;
        json["supervision"] = new JsonObject
        {
            ["project"] = Desk,
            ["workMachines"] = new JsonArray("work-laptop"),
            ["outboundPaths"] = new JsonArray(Outbound.Replace('\\', '/')),
        };
        await File.WriteAllTextAsync(path, json.ToJsonString(), ct);
    }

    public async ValueTask DisposeAsync()
    {
        _memory.Dispose();
        await _harness.DisposeAsync();
    }

    // AC-25: the guard refuses a file-tool read and a shell command naming a path outside the root, and allows reads inside it.
    [Fact]
    public async Task WorkMachineQuestion_ReadsOutsideTheRootRefusedAndCounted_InsideAllowed_Answered()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        (EnvelopeId question, string questionPath) = await SeedQuestionAsync(WorkLaptop, ct);
        Script(question, questionPath);

        // Act
        int exit = await _harness.RunUntilAsync(async () => (await DecisionsAsync(ct)).Count == 1, Limit, ct, _model.Register);

        // Assert
        exit.Should().Be(0, Log());
        Model decision = (await DecisionsAsync(ct)).Single();
        string[] blocked = await File.ReadAllLinesAsync(TranscriptPaths.BlockedFor(_harness.NodeDir, Acme, question), ct);
        new
        {
            Tools = await File.ReadAllLinesAsync(_model.ToolLog, ct),
            Blocked = blocked.Select(l => l.Contains(": read outside the read root (", StringComparison.Ordinal)).ToArray(),
            decision.Decision!.Kind,
            decision.Header.InReplyTo,
            decision.Header.To,
            QuestionArchived = !await _harness.Bus.ExistsOnMainAsync(questionPath, ct),
        }.Should().BeEquivalentTo(new
        {
            Tools = OutsideRefusedInsideAllowed,
            Blocked = BothByTheReadRoot,
            Kind = DecisionKind.Answer,
            InReplyTo = (EnvelopeId?)question,
            To = WorkLaptop,
            QuestionArchived = true,
        }, Log());
    }

    // AC-25: the run answering a work machine has the root at outboundPaths (plus the one question) and no Hub tool.
    [Fact]
    public async Task WorkMachineQuestion_RunGetsTheReadRootsAndNoHubTool()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        (EnvelopeId question, string questionPath) = await SeedQuestionAsync(WorkLaptop, ct);
        Script(question, questionPath);

        // Act
        int exit = await _harness.RunUntilAsync(async () => (await DecisionsAsync(ct)).Count == 1, Limit, ct, _model.Register);

        // Assert
        exit.Should().Be(0, Log());
        ModelRunRequest request = _model.Runs.Single().Request;
        new
        {
            ReadRoots = OptionValues(request, "--read-root").Select(Normal).ToArray(),
            HubAllowed = request.AllowedTools.Contains("mcp__hub__get_context") || OptionValues(request, "--allow").Contains("mcp__hub__get_context"),
            request.DisallowedTools,
        }.Should().BeEquivalentTo(new
        {
            ReadRoots = new[] { Normal(Outbound), Normal(Path.Combine(_harness.Bus.CloneDir, questionPath)) },
            HubAllowed = false,
            DisallowedTools = HubDenied,
        }, Log());
    }

    // AC-25: a canary line in a file outside the root never appears in the decision.
    [Fact]
    public async Task WorkMachineQuestion_CanaryOutsideTheRoot_NotInTheDecisionNorOnTheBus()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        (EnvelopeId question, _) = await SeedQuestionAsync(WorkLaptop, ct);

        // The run reads the canary file, then the outbound note, and writes everything it could read into its decision.
        _model.Script = (_, _) => new Step(
        [
            SupervisorScript.Call("Read", new JsonObject { ["file_path"] = CanaryFile }),
            SupervisorScript.Call("Read", new JsonObject { ["file_path"] = OutboundNote }),
            SupervisorScript.Zyggy("decide", "--reply-to", question.Value, "--kind", "answer", "--basis", "reported", "--undo", "nothing to undo", "Read: {read}"),
        ]);

        // Act
        int exit = await _harness.RunUntilAsync(async () => (await DecisionsAsync(ct)).Count == 1, Limit, ct, _model.Register);

        // Assert: only the outbound note reached the decision
        exit.Should().Be(0, Log());
        string decisionPath = (await FilesOnMainAsync(BusPaths.Decisions(Acme, Central), ct)).Single();
        string onBus = Encoding.UTF8.GetString(await _harness.Bus.ShowBytesAsync(decisionPath, ct));
        new
        {
            Text = (await DecisionsAsync(ct)).Single().BodyText,
            InDecision = onBus.Contains("CANARY", StringComparison.Ordinal),
            InLog = Log().Contains("CANARY", StringComparison.Ordinal),
        }.Should().BeEquivalentTo(new { Text = "Read: - Anna is the contact for the booking pool\n", InDecision = false, InLog = false }, Log());
    }

    // A machine that is not a work machine is answered as before: no read root, the Hub tool in the list.
    [Fact]
    public async Task OtherMachineQuestion_NoReadRoot_HubToolKept_ReadOutsideOutboundAllowed()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        (EnvelopeId question, string questionPath) = await SeedQuestionAsync(HomeLaptop, ct);
        Script(question, questionPath);

        // Act
        int exit = await _harness.RunUntilAsync(async () => (await DecisionsAsync(ct)).Count == 1, Limit, ct, _model.Register);

        // Assert
        exit.Should().Be(0, Log());
        ModelRunRequest request = _model.Runs.Single().Request;
        new
        {
            ReadRoots = OptionValues(request, "--read-root").Count,
            Hub = OptionValues(request, "--allow").Contains("mcp__hub__get_context"),
            Disallowed = request.DisallowedTools.Count,
            FirstRead = (await File.ReadAllLinesAsync(_model.ToolLog, ct))[0],
        }.Should().BeEquivalentTo(new { ReadRoots = 0, Hub = true, Disallowed = 0, FirstRead = "allowed Read" }, Log());
    }

    private static string Normal(string path) => Path.GetFullPath(path).Replace('\\', '/');

    private static List<string> OptionValues(ModelRunRequest request, string option)
    {
        IReadOnlyList<string> args = request.PreToolUseHook!.Args;
        return [.. Enumerable.Range(0, args.Count - 1).Where(i => args[i] == option).Select(i => args[i + 1])];
    }

    private string Log() => string.Join('\n', _harness.LogLines);

    /// <summary>The run tries the canary file with a file tool and through a shell command, reads the outbound note and its question, then answers.</summary>
    private void Script(EnvelopeId question, string questionPath)
    {
        string questionFile = Path.Combine(_harness.Bus.CloneDir, questionPath).Replace('\\', '/');
        _model.Script = (_, _) => new Step(
        [
            SupervisorScript.Call("Read", new JsonObject { ["file_path"] = CanaryFile }),
            SupervisorScript.Call("Bash", new JsonObject { ["command"] = "zyggy report '" + CanaryFile + "'" }),
            SupervisorScript.Call("Read", new JsonObject { ["file_path"] = OutboundNote }),
            SupervisorScript.Call("Read", new JsonObject { ["file_path"] = questionFile }),
            SupervisorScript.Zyggy("decide", "--reply-to", question.Value, "--kind", "answer", "--basis", "reported", "--undo", "nothing to undo", "Anna is the contact."),
        ]);
    }

    private async Task<(EnvelopeId Id, string Path)> SeedQuestionAsync(MachineName from, CancellationToken ct)
    {
        DateTimeOffset created = DateTimeOffset.UtcNow.AddMinutes(-5);
        EnvelopeId id = EnvelopeId.New(created);
        Model question = Model.CreateQuestion(
            new EnvelopeHeader(Acme, id, from, Central, created),
            new QuestionFields("calizr", "wait for the owner", ["Anna", "Bram"], Session: "a1b2c3d4"),
            Encoding.UTF8.GetBytes("Who is the contact for the booking pool?\n"));
        EnvelopeResult signed = await new EnvelopeSigner(_harness.Store).SignAsync(question, TestKeys.Acme1, ct);
        signed.IsAccepted.Should().BeTrue(signed.Rejection?.ToString());
        string path = BusPaths.EnvelopeFile(BusPaths.Questions(Acme, from), id);
        await _harness.Bus.SeedRawFileAsync(path, EnvelopeWriter.Serialize(signed.Envelope!), $"acme question {id.Value} {from.Value}→central", ct);
        return (id, path);
    }

    private async Task<IReadOnlyList<Model>> DecisionsAsync(CancellationToken ct)
    {
        var decisions = new List<Model>();
        foreach (string path in await FilesOnMainAsync(BusPaths.Decisions(Acme, Central), ct))
        {
            EnvelopeResult parsed = await new EnvelopeSigner(_harness.Store).ParseAndVerifyAsync(await _harness.Bus.ShowBytesAsync(path, ct), Acme, ct);
            parsed.IsAccepted.Should().BeTrue(parsed.Rejection?.ToString());
            decisions.Add(parsed.Envelope!);
        }

        return decisions;
    }

    private async Task<IReadOnlyList<string>> FilesOnMainAsync(string folder, CancellationToken ct)
    {
        string listing = await _harness.Bus.RunGitAsync(_harness.Bus.BareDir, ["ls-tree", "-r", "--name-only", "main", "--", folder], ct);
        return [.. listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.EndsWith(".md", StringComparison.Ordinal) && string.Equals(p[..p.LastIndexOf('/')], folder, StringComparison.Ordinal))];
    }
}
