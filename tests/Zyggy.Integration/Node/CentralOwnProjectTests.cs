using System.Text;
using System.Text.Json.Nodes;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Registry;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;
using Step = Zyggy.Integration.Infrastructure.SupervisorStepScript;

namespace Zyggy.Integration.Node;

/// <summary>
/// Spec 55 slice 2 (AC-3) through the production host as <c>central</c> on a Central-shaped tree: the parent folder is the one
/// <c>devRoots</c> entry, below it lie the instance project and the service user's home folder (which holds <c>.claude/</c> and a
/// project of its own), <c>discovery.exclude</c> names the home folder and <c>supervision.project</c> the instance project. The node
/// publishes exactly the instance project, and a question on the bus starts one supervisor run in that folder.
/// </summary>
public sealed class CentralOwnProjectTests : IAsyncLifetime
{
    private const string Instance = "instance";

    private const string Home = "home";

    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName Central = MachineName.Central;

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(90);

    private static readonly string[] OnlyTheInstance = [Instance];

    private NodeHostHarness _harness = null!;

    private PickupMemory _memory = null!;

    private SupervisorScript _model = null!;

    private string InstancePath => Path.Combine(_harness.ProjectsRoot, Instance);

    public async ValueTask InitializeAsync()
    {
        CancellationToken ct = CancellationToken.None;
        _harness = await NodeHostHarness.CreateAsync(ct);
        _model = new SupervisorScript(_harness);
        _memory = new PickupMemory();
        _harness.Environment = _memory.Env();
        await _harness.ProjectAsync(ct, name: Instance);

        // The service user's home: Claude Code's own .claude/ and a clone below it, both of which a scan would otherwise publish.
        string home = Path.Combine(_harness.ProjectsRoot, Home);
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        Directory.CreateDirectory(Path.Combine(home, "zyggy-core", ".claude"));

        string path = _harness.WriteNodeJson(machine: Central, devRoots: [_harness.ProjectsRoot], discoveryJson: "{ \"exclude\": [\"" + Home + "\"] }");
        JsonNode json = JsonNode.Parse(await File.ReadAllTextAsync(path, ct))!;
        json["supervision"] = new JsonObject { ["project"] = Instance };
        await File.WriteAllTextAsync(path, json.ToJsonString(), ct);
    }

    public async ValueTask DisposeAsync()
    {
        _memory.Dispose();
        await _harness.DisposeAsync();
    }

    // AC-3: the parent is listed, the home folder excluded: the registry holds exactly the instance project.
    [Fact]
    public async Task Start_CentralShapedTree_PublishesOnlyTheInstanceProject()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;

        // Act
        int exit = await _harness.RunUntilAsync(async () => await _harness.RegistryOnMainAsync(Central, ct) is not null, Limit, ct, _model.Register);

        // Assert
        exit.Should().Be(0, Log());
        RegistryDocument registry = (await _harness.RegistryOnMainAsync(Central, ct))!;
        registry.Projects.Select(p => p.Name).Should().Equal(OnlyTheInstance, Log());
    }

    // AC-3: supervision.project names the published instance project: a question starts one supervisor run in that folder.
    [Fact]
    public async Task Question_CentralShapedTree_OneSupervisorRunInTheInstanceFolder()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        EnvelopeId question = await SeedQuestionAsync(ct);
        _model.Script = (_, _) => new Step(
        [
            SupervisorScript.Zyggy("decide", "--reply-to", question.Value, "--kind", "answer", "--basis", "reported", "--undo", "nothing to undo", "Anna is the contact."),
        ]);

        // Act
        int exit = await _harness.RunUntilAsync(async () => (await DecisionsAsync(ct)).Count == 1, Limit, ct, _model.Register);

        // Assert
        exit.Should().Be(0, Log());
        Model decision = (await DecisionsAsync(ct)).Single();
        new
        {
            Runs = _model.Calls,
            Folder = Normal(_model.Runs.Single().Request.WorkingDirectory),
            decision.Decision!.Kind,
            decision.Header.InReplyTo,
        }.Should().BeEquivalentTo(new
        {
            Runs = 1,
            Folder = Normal(InstancePath),
            Kind = DecisionKind.Answer,
            InReplyTo = (EnvelopeId?)question,
        }, Log());
    }

    private static string Normal(string path) => Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/');

    private string Log() => string.Join('\n', _harness.LogLines);

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
