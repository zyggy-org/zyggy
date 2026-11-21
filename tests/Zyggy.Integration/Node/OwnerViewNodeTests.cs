using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Brief;
using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Registry;
using Zyggy.Core.Supervision;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;
using Step = Zyggy.Integration.Infrastructure.SupervisorStepScript;

namespace Zyggy.Integration.Node;

/// <summary>
/// Spec 18 slice 13 through the production host as <c>central</c>, with an M365 instance and the stub Graph over a bare repository: a
/// paused mandate puts one item on the owner's list and the node mails it — one <c>sendMail</c> from the owner's own mailbox to itself,
/// with the fixed text and no report text (AC-29); a failed send is logged once per streak and tried again while the node runs on
/// (AC-29); the node hands its decisions to the morning brief; with the owner's stop marker no supervisor run starts until it is gone (AC-30).
/// </summary>
public sealed class OwnerViewNodeTests : IAsyncLifetime
{
    private const string Project = "shop";

    private const string Desk = "central-desk";

    private const string Mailbox = "alice@acme.example";

    private const string SendRoute = "/v1\\.0/users/[^/]+/sendMail$";

    private const string Marker = "CANARY-77b0-never-in-the-mail";

    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName Central = MachineName.Central;

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(90);

    private NodeHostHarness _harness = null!;

    private PickupArchiveMemory _memory = null!;

    private SupervisorScript _model = null!;

    private int _sequence;

    private DateTimeOffset? _quietSince;

    private string DecidedFile => new BriefPaths(_memory.Env()).Decided;

    public async ValueTask InitializeAsync()
    {
        CancellationToken ct = CancellationToken.None;
        _memory = new PickupArchiveMemory(m365: true);
        await _memory.InitializeAsync();
        _memory.Graph.AllowOwnerMail(Mailbox);
        _harness = await NodeHostHarness.CreateAsync(ct);
        _model = new SupervisorScript(_harness);
        _harness.Environment = _memory.Env();
        await _harness.ProjectAsync(ct, name: Desk);
        string path = _harness.WriteNodeJson(machine: Central);
        JsonNode json = JsonNode.Parse(File.ReadAllText(path))!;
        json["supervision"] = new JsonObject { ["project"] = Desk };
        File.WriteAllText(path, json.ToJsonString());
        var registry = new RegistryDocument(Acme, HomeLaptop, RegistryOs.Windows, DateTimeOffset.UtcNow.AddMinutes(-1), null, null, [])
        {
            Capabilities = [RegistryDocument.SupervisionCapability],
        };
        await _harness.Bus.SeedRawFileAsync(BusPaths.Registry(Acme, HomeLaptop), registry.Serialize(), "seed: home-laptop registry", ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
        await _memory.DisposeAsync();
    }

    // AC-29: one message, from and to the owner's own mailbox, with the fixed subject and line and nothing an agent wrote.
    [Fact]
    public async Task Mail_PausedMandate_OneSendMailToTheOwnersOwnMailbox_FixedText()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        EnvelopeId mandate = await SeedPausedMandateAsync(ct);

        // Act
        int exit = await _harness.RunUntilAsync(async () => Sends().Count == 1 && await QuietForAsync(TimeSpan.FromSeconds(4)), Limit, ct, Services);

        // Assert
        exit.Should().Be(0, Log());
        RecordedRequest send = Sends().Should().ContainSingle(Log()).Subject;
        using JsonDocument body = JsonDocument.Parse(send.Body!);
        JsonElement message = body.RootElement.GetProperty("message");
        string content = message.GetProperty("body").GetProperty("content").GetString()!;
        new
        {
            Url = send.Uri.ToString(),
            Subject = message.GetProperty("subject").GetString(),
            Line = content.StartsWith("mandate, home-laptop, " + Project + ", " + mandate.Value + ", ", StringComparison.Ordinal) && content.Count(c => c == '\n') == 1,
            To = message.GetProperty("toRecipients")[0].GetProperty("emailAddress").GetProperty("address").GetString(),
            Saved = body.RootElement.GetProperty("saveToSentItems").GetBoolean(),
            Leak = send.Body!.Contains(Marker, StringComparison.Ordinal),
        }.Should().BeEquivalentTo(new
        {
            Url = "https://graph.microsoft.com/v1.0/users/" + Mailbox + "/sendMail",
            Subject = "Zyggy: 1 item(s) wait for you",
            Line = true,
            To = Mailbox,
            Saved = false,
            Leak = false,
        }, content + Log());
    }

    // AC-29: two refused sends are one Error line; the send is tried again and goes out while the node keeps running.
    [Fact]
    public async Task Mail_SendFailsTwice_OneErrorPerStreak_SentOnTheThirdTry()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        _memory.Graph.Once("POST", SendRoute, 500, "{\"error\":{\"code\":\"generalException\"}}").Once("POST", SendRoute, 503, "{}");
        await SeedPausedMandateAsync(ct);

        // Act
        int exit = await _harness.RunUntilAsync(() => Task.FromResult(Sends().Count == 3), Limit, ct, s =>
        {
            Services(s);
            s.Configure<OwnerMailOptions>(o =>
            {
                o.Tick = TimeSpan.FromMilliseconds(200);
                o.Retry = TimeSpan.FromSeconds(1);
            });
        });

        // Assert
        exit.Should().Be(0, Log());
        new { Errors = _harness.LogLines.Count(l => l.Contains("owner mail: send failed", StringComparison.Ordinal)), Statuses = Sends().Count }
            .Should().BeEquivalentTo(new { Errors = 1, Statuses = 3 }, Log());
    }

    // "Record": the node-written escalate of a pause reaches the brief's decided.json as kind, machine, project and id.
    [Fact]
    public async Task Brief_NodeWrittenDecision_HandedToTheBriefWithoutItsText()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await SeedPausedMandateAsync(ct);

        // Act
        int exit = await _harness.RunUntilAsync(() => Task.FromResult(File.Exists(DecidedFile)), Limit, ct, Services);

        // Assert
        exit.Should().Be(0, Log());
        Model decision = (await DecisionsAsync(ct)).Should().ContainSingle(Log()).Subject;
        new { Lines = Zyggy.Core.Supervision.DecidedFile.Read(DecidedFile, DateTimeOffset.MinValue, TimeZoneInfo.Utc).Select(l => (l.Kind, l.Machine, l.Project, l.Id)).ToArray(), Leak = File.ReadAllText(DecidedFile).Contains(Marker, StringComparison.Ordinal) }
            .Should().BeEquivalentTo(new { Lines = new[] { ("escalate", "home-laptop", (string?)Project, decision.Header.Id.Value) }, Leak = false }, Log());
    }

    // AC-30: with the owner's stop marker, a question starts no supervisor run.
    [Fact]
    public async Task Wake_Stopped_NoRunOnAQuestion()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        SupervisionSwitch.Stop(_harness.NodeDir, DateTimeOffset.UtcNow);
        await SeedQuestionAsync(ct);
        _model.Script = (_, id) => new Step([Answer(id)]);

        // Act
        int exit = await _harness.RunUntilAsync(
            async () => _harness.LogLines.Any(l => l.Contains("stopped by the owner", StringComparison.Ordinal)) && await QuietForAsync(TimeSpan.FromSeconds(4)),
            Limit,
            ct,
            Services);

        // Assert
        exit.Should().Be(0, Log());
        new { Runs = _model.Calls, Decisions = (await DecisionsAsync(ct)).Count }.Should().BeEquivalentTo(new { Runs = 0, Decisions = 0 }, Log());
    }

    // AC-30: once the marker is gone, the question gets its run.
    [Fact]
    public async Task Wake_StoppedThenStarted_QuestionAnswered()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        SupervisionSwitch.Stop(_harness.NodeDir, DateTimeOffset.UtcNow);
        SupervisionSwitch.Start(_harness.NodeDir);
        EnvelopeId question = await SeedQuestionAsync(ct);
        _model.Script = (_, id) => new Step([Answer(id)]);

        // Act
        int exit = await _harness.RunUntilAsync(async () => (await DecisionsAsync(ct)).Count == 1, Limit, ct, Services);

        // Assert
        exit.Should().Be(0, Log());
        Model decision = (await DecisionsAsync(ct)).Single();
        new { decision.Decision!.Kind, decision.Header.InReplyTo, Runs = _model.Calls }
            .Should().BeEquivalentTo(new { Kind = DecisionKind.Answer, InReplyTo = (EnvelopeId?)question, Runs = 1 }, Log());
    }

    private List<RecordedRequest> Sends() => [.. _memory.Graph.To(SendRoute)];

    private string Log() => string.Join('\n', _harness.LogLines);

    private void Services(IServiceCollection services)
    {
        _model.Register(services);
        services.AddSingleton(_memory.DriveOptions);
    }

    private Task<bool> QuietForAsync(TimeSpan time)
    {
        _quietSince ??= DateTimeOffset.UtcNow;
        return Task.FromResult(DateTimeOffset.UtcNow - _quietSince > time);
    }

    private static string Answer(string questionId) =>
        SupervisorScript.Zyggy("decide", "--reply-to", questionId, "--kind", "answer", "--basis", "reported", "--undo", "nothing to undo", "Keep the current layout.");

    /// <summary>A mandate whose three newest reports failed: paused, so the node writes its escalate and the owner's list holds it.</summary>
    private async Task<EnvelopeId> SeedPausedMandateAsync(CancellationToken ct)
    {
        DateTimeOffset granted = DateTimeOffset.UtcNow.AddHours(-1);
        EnvelopeId id = EnvelopeId.New(granted);
        var fields = new MandateFields(Project, "Build the shop", ["Read", "Edit"], 10, granted.AddDays(3), Spec: "_specs/12-shop.md", SpecCommit: "0123456789abcdef0123456789abcdef01234567");
        Model mandate = Model.CreateMandate(new EnvelopeHeader(Acme, id, Central, HomeLaptop, granted), fields);
        EnvelopeResult signed = await new EnvelopeSigner(_harness.Store).SignAsync(mandate, TestKeys.Acme1, ct);
        signed.IsAccepted.Should().BeTrue(signed.Rejection?.ToString());
        await _harness.Bus.SeedRawFileAsync(BusPaths.EnvelopeFile(BusPaths.Mandates(Acme), id), EnvelopeWriter.Serialize(signed.Envelope!), "acme mandate " + id.Value + " central→home-laptop", ct);
        for (var minutes = 15; minutes >= 5; minutes -= 5)
        {
            DateTimeOffset created = DateTimeOffset.UtcNow.AddMinutes(-minutes).AddMilliseconds(++_sequence);
            await _harness.Bus.SeedSignedReportAsync(
                HomeLaptop,
                Central,
                EnvelopeId.New(created.AddMinutes(-2)),
                TestKeys.Acme1,
                _harness.Store,
                "claude_error: " + Marker + "\n",
                created: created,
                fields: new ReportFields(ReportStatus.Failed, "claude_error", NumTurns: 3, Model: "claude-test", Mandate: id, Step: "slice 1"), // spec 59: only a run where the model started counts toward the pause
                ct: ct);
        }

        return id;
    }

    private async Task<EnvelopeId> SeedQuestionAsync(CancellationToken ct)
    {
        DateTimeOffset at = DateTimeOffset.UtcNow.AddMinutes(-2);
        EnvelopeId id = EnvelopeId.New(at);
        Model question = Model.CreateQuestion(new EnvelopeHeader(Acme, id, HomeLaptop, Central, at), new QuestionFields(Project, "keep it", [], Session: "5b3fd3ac"), "Which layout?\n"u8.ToArray());
        EnvelopeResult signed = await new EnvelopeSigner(_harness.Store).SignAsync(question, TestKeys.Acme1, ct);
        signed.IsAccepted.Should().BeTrue(signed.Rejection?.ToString());
        await _harness.Bus.SeedRawFileAsync(BusPaths.EnvelopeFile(BusPaths.Questions(Acme, HomeLaptop), id), EnvelopeWriter.Serialize(signed.Envelope!), "acme question " + id.Value + " home-laptop→central", ct);
        return id;
    }

    private async Task<IReadOnlyList<Model>> DecisionsAsync(CancellationToken ct)
    {
        string listing = await _harness.Bus.RunGitAsync(_harness.Bus.BareDir, ["ls-tree", "-r", "--name-only", "main", "--", BusPaths.Decisions(Acme, Central)], ct);
        var decisions = new List<Model>();
        foreach (string path in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(p => p.EndsWith(".md", StringComparison.Ordinal)))
        {
            EnvelopeResult parsed = await new EnvelopeSigner(_harness.Store).ParseAndVerifyAsync(await _harness.Bus.ShowBytesAsync(path, ct), Acme, ct);
            parsed.IsAccepted.Should().BeTrue(parsed.Rejection?.ToString());
            decisions.Add(parsed.Envelope!);
        }

        return decisions;
    }
}
