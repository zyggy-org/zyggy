using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Zyggy.Core.M365;
using Zyggy.Core.M365.Audit;
using Zyggy.Core.Memory;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.M365;

/// <summary>
/// The post-run audit of the brief's Drafts — <c>verify.sh</c> (spec 33 AC-23; the four <c>verify:</c> bats cases) over the stubbed Graph.
/// </summary>
public sealed class DraftAuditTests : IDisposable
{
    private const string Window = "2026-09-30T04:00:00Z";
    private const string DraftsRoute = "mailFolders/drafts/messages\\?\\$filter";
    private static readonly DateOnly Date = new(2026, 9, 30);

    private readonly GraphFixture _graph = new();
    private readonly string _stateRoot = Path.Combine(Path.GetTempPath(), "zyggy-ut", Guid.NewGuid().ToString("N"));

    private string StateDirectory => Path.Combine(_stateRoot, "m365");

    private string Receipt => Path.Combine(StateDirectory, "brief-2026-09-30.json");

    public void Dispose()
    {
        _graph.Stub.Violations.Should().BeEmpty();
        _graph.Dispose();
        if (Directory.Exists(_stateRoot))
        {
            Directory.Delete(_stateRoot, recursive: true);
        }
    }

    public static TheoryData<string, string?, string[]> Cases() => new()
    {
        { "audit ok", "m1", [BriefOk(), ReplyOk()] },
        { "audit FLAGGED: draft \"Zyggy — note\" to mallory@external.example not allowed", "m1", [BriefOk(), ReplyOk(), Draft("d5", "Zyggy — note", "c5", "Hello.", "mallory@external.example")] },
        { "audit FLAGGED: draft \"RE: Invoice 2026-41\" to mallory@external.example not allowed", "m1", [BriefOk(), Draft("d1", "RE: Invoice 2026-41", "c1", "Dear Carol, I will call tomorrow.", "carol@example.org,mallory@external.example")] },
        { "audit ok", "m2", [BriefOk(), Draft("d2", "RE: Newsletter 39", "c2", "Thanks.", "erin@example.org,dave@example.org")] },
        { "audit FLAGGED: reply draft \"RE: Invoice 2026-41\" has no recorded replied message", null, [BriefOk(), ReplyOk()] },
        { "audit FLAGGED: reply draft \"RE: Invoice 2026-41\" has no recorded replied message", "m2", [BriefOk(), ReplyOk()] },
        {
            "audit FLAGGED: 5 drafts > cap 4 (one brief + reply_cap 3)", "m1",
            [BriefOk(), ReplyOk(), Draft("d2", "RE: Invoice 2026-41", "c1", "Second note.", "carol@example.org"),
             Draft("d3", "RE: Invoice 2026-41", "c1", "Third note.", "carol@example.org"), Draft("d4", "RE: Invoice 2026-41", "c1", "Fourth note.", "carol@example.org")]
        },
        { "audit FLAGGED: brief draft has recipients other than the owner (carol@example.org)", "m1", [Draft("d0", "Zyggy — morning brief 2026-09-30", "c0", "Mail.", "alice@acme.example", "carol@example.org"), ReplyOk()] },
        { "audit FLAGGED: 2 brief drafts", "m1", [BriefOk(), ReplyOk(), Draft("d9", "Zyggy — morning brief 2026-09-30", "c9", "Again.", "alice@acme.example")] },
        { "audit FLAGGED: no brief draft", "m1", [ReplyOk()] },
        { "audit FLAGGED: draft \"RE: Invoice 2026-41\" matches secret pattern iban", "m1", [BriefOk(), Draft("d1", "RE: Invoice 2026-41", "c1", "Please pay to BE71 0961 2345 6769 by Friday.", "carol@example.org")] },
        { "audit FLAGGED: draft \"RE: Invoice 2026-41\" contains an e-mail address", "m1", [BriefOk(), Draft("d1", "RE: Invoice 2026-41", "c1", "Write to dave@example.org instead.", "carol@example.org")] },
        { "audit FLAGGED: draft \"RE: Invoice 2026-41\" contains a URL", "m1", [BriefOk(), Draft("d1", "RE: Invoice 2026-41", "c1", "See www.example.org for the terms.", "carol@example.org")] },
        {
            "audit ok", "m1",
            [BriefOk(), Draft("d1", "RE: Invoice 2026-41", "c1", "Dear Carol, I will call tomorrow.\r\n\r\n________________________________\r\nFrom: Carol <carol@example.org>\r\nSee https://example.org/invoice", "carol@example.org")]
        },
    };

    [Fact]
    public async Task Audit_DraftsOkRepliedM1_OkReceiptByteEqualsExpected()
    {
        // Arrange
        Replied("m1");

        // Act
        var outcome = await Audit().AuditAsync(Date, Window, CancellationToken.None);

        // Assert
        outcome.Should().Be(new AuditOutcome(0, "audit ok", null));
        File.ReadAllBytes(Receipt).Should().Equal(File.ReadAllBytes(M365Run.Golden("expected", "m365-receipt-ok.json")));
        Directory.EnumerateFiles(StateDirectory, "*.tmp").Should().BeEmpty();
        _graph.Urls.Where(u => u.StartsWith("GET", StringComparison.Ordinal)).Should().Equal(
            "GET https://graph.microsoft.com/v1.0/users/alice@acme.example/mailFolders/drafts/messages?$filter=createdDateTime ge 2026-09-30T04:00:00Z" +
            "&$select=id,subject,toRecipients,ccRecipients,bccRecipients,conversationId,createdDateTime,changeKey,body&$top=50",
            "GET https://graph.microsoft.com/v1.0/users/alice@acme.example/messages/m1?$select=from,replyTo,conversationId");
    }

    [Fact]
    public async Task Audit_DraftsFlagged_FlaggedReasonsInDraftOrder()
    {
        // Arrange
        Replied("m1");
        _graph.Stub.Once("GET", DraftsRoute, 200, File.ReadAllText(StubGraphHandler.GraphFixture("drafts-flagged.json")));

        // Act
        var outcome = await Audit().AuditAsync(Date, Window, CancellationToken.None);

        // Assert
        outcome.Exit.Should().Be(5);
        outcome.StdoutLine.Should().Be(
            "audit FLAGGED: brief draft has recipients other than the owner (mallory@external.example); draft \"Zyggy — morning brief 2026-09-30\" contains a URL; draft \"RE: Invoice 2026-41\" contains a URL");
        var receipt = JsonDocument.Parse(File.ReadAllText(Receipt)).RootElement;
        receipt.GetProperty("audit").GetString().Should().Be("flagged");
        receipt.GetProperty("reasons").GetArrayLength().Should().Be(3);
        receipt.GetProperty("drafts")[0].GetProperty("recipients").EnumerateArray().Select(r => r.GetString()).Should().Equal("alice@acme.example", "mallory@external.example");
        receipt.GetProperty("drafts").EnumerateArray().Should().OnlyContain(d => d.EnumerateObject().Select(p => p.Name).SequenceEqual(DraftKeys));
        File.ReadAllText(Receipt).Should().NotContain("example.org/pay");
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Audit_DraftCases(string expected, string? replied, string[] drafts)
    {
        // Arrange
        if (replied is not null)
        {
            Replied(replied);
        }

        _graph.Stub.Once("GET", DraftsRoute, 200, "{\"value\":[" + string.Join(',', drafts) + "]}");

        // Act
        var outcome = await Audit().AuditAsync(Date, Window, CancellationToken.None);

        // Assert
        outcome.StdoutLine.Should().Be(expected);
        outcome.Exit.Should().Be(expected == "audit ok" ? 0 : 5);
        var receipt = JsonDocument.Parse(File.ReadAllText(Receipt)).RootElement;
        receipt.GetProperty("audit").GetString().Should().Be(expected == "audit ok" ? "ok" : "flagged");
        string.Join("; ", receipt.GetProperty("reasons").EnumerateArray().Select(r => r.GetString())).Should().Be(expected == "audit ok" ? string.Empty : expected["audit FLAGGED: ".Length..]);
        File.ReadAllText(Receipt).Should().NotContain("0961");
    }

    public static TheoryData<string, string[], string[]> SessionCases() => new()
    {
        { "audit ok", [ReplyOk()], [] },
        { "audit ok", [], [] },
        { "audit FLAGGED: 1 brief draft (the brief is shown in the session)", [BriefOk(), ReplyOk()], [] },
        { "audit FLAGGED: 2 brief drafts (the brief is shown in the session)", [BriefOk(), Draft("d9", "Zyggy — morning brief 2026-09-30", "c9", "Again.", "alice@acme.example")], [] },
        { "audit FLAGGED: reply draft \"RE: Invoice 2026-41\" answers a mail the owner already answered", [ReplyOk()], ["c1"] },
        {
            "audit FLAGGED: 4 drafts > reply_cap 3",
            [ReplyOk(), Draft("d2", "RE: Invoice 2026-41", "c1", "Second.", "carol@example.org"), Draft("d3", "RE: Invoice 2026-41", "c1", "Third.", "carol@example.org"), Draft("d4", "RE: Invoice 2026-41", "c1", "Fourth.", "carol@example.org")],
            []
        },
        { "audit FLAGGED: draft \"RE: Invoice 2026-41\" contains a URL", [Draft("d1", "RE: Invoice 2026-41", "c1", "See www.example.org for the terms.", "carol@example.org")], [] },
    };

    [Theory]
    [MemberData(nameof(SessionCases))]
    public async Task Audit_SessionCases(string expected, string[] drafts, string[] answered)
    {
        // Arrange: spec 35 AC-20 — no brief Draft expected, cap = reply_cap, an answered conversation may not get a reply Draft
        Replied("m1");
        _graph.Stub.Once("GET", DraftsRoute, 200, "{\"value\":[" + string.Join(',', drafts) + "]}");

        // Act
        var outcome = await Audit().AuditAsync(Date, Window, AuditMode.Session, answered.ToHashSet(StringComparer.Ordinal), [], CancellationToken.None);

        // Assert
        outcome.StdoutLine.Should().Be(expected);
        outcome.Exit.Should().Be(expected == "audit ok" ? 0 : 5);
        JsonDocument.Parse(File.ReadAllText(Receipt)).RootElement.GetProperty("audit").GetString().Should().Be(expected == "audit ok" ? "ok" : "flagged");
    }

    [Fact]
    public async Task Audit_SessionViolations_JoinTheVerdictAfterTheDraftReasons()
    {
        // Arrange
        Replied("m1");
        _graph.Stub.Once("GET", DraftsRoute, 200, "{\"value\":[" + BriefOk() + "]}");

        // Act
        var outcome = await Audit().AuditAsync(Date, Window, AuditMode.Session, new HashSet<string>(StringComparer.Ordinal), ["link withheld in summary of m09", "send for m04 dropped: its draft is not a reply in Drafts"], CancellationToken.None);

        // Assert
        outcome.Exit.Should().Be(5);
        outcome.StdoutLine.Should().Be("audit FLAGGED: 1 brief draft (the brief is shown in the session); link withheld in summary of m09; send for m04 dropped: its draft is not a reply in Drafts");
        outcome.Reasons.Should().HaveCount(3);
        JsonDocument.Parse(File.ReadAllText(Receipt)).RootElement.GetProperty("reasons").GetArrayLength().Should().Be(3);
    }

    [Fact]
    public async Task Audit_RepliedIdNotFound_SkippedKeptInReceipt()
    {
        // Arrange
        Replied("m7");
        _graph.Stub.Once("GET", "messages/m7\\?", 404, File.ReadAllText(StubGraphHandler.GraphFixture("graph-not-found.json")));

        // Act
        var outcome = await Audit().AuditAsync(Date, Window, CancellationToken.None);

        // Assert
        outcome.Should().Be(new AuditOutcome(5, "audit FLAGGED: reply draft \"RE: Invoice 2026-41\" has no recorded replied message", null));
        JsonDocument.Parse(File.ReadAllText(Receipt)).RootElement.GetProperty("replied_ids").EnumerateArray().Select(r => r.GetString()).Should().Equal("m7");
    }

    [Theory]
    [InlineData(DraftsRoute)]
    [InlineData("messages/m1\\?")]
    public async Task Audit_Graph403_ExitSixNoReceipt(string route)
    {
        // Arrange
        Replied("m1");
        _graph.Stub.Once("GET", route, 403, File.ReadAllText(StubGraphHandler.GraphFixture("graph-forbidden.json")));

        // Act
        var outcome = await Audit().AuditAsync(Date, Window, CancellationToken.None);

        // Assert
        outcome.Should().Be(new AuditOutcome(6, null, "forbidden (ErrorAccessDenied) — runbook 13 \"Scope or grant missing\""));
        File.Exists(Receipt).Should().BeFalse();
    }

    [Fact]
    public async Task Audit_InvalidClient_ExitSixNoRead()
    {
        // Arrange
        _graph.Stub.Once("POST", "/oauth2/v2\\.0/token$", 400, File.ReadAllText(StubGraphHandler.GraphFixture("token-invalid-client.json")));

        // Act
        var outcome = await Audit().AuditAsync(Date, Window, CancellationToken.None);

        // Assert
        outcome.Exit.Should().Be(6);
        outcome.Error.Should().StartWith("auth failed (invalid_client)");
        _graph.Stub.Requests.Should().NotContain(r => r.Method == HttpMethod.Get);
        File.Exists(Receipt).Should().BeFalse();
    }

    [Fact]
    public async Task Audit_ReceiptHasNoBodyText()
    {
        // Arrange
        Replied("m1");

        // Act
        await Audit().AuditAsync(Date, Window, CancellationToken.None);

        // Assert
        File.ReadAllText(Receipt).Should().NotContainAny("Dear Carol", "## Mail", "asks for the date");
    }

    private static readonly string[] DraftKeys = ["id", "kind", "subject", "recipients"];

    private static string BriefOk() => Draft("d0", "Zyggy — morning brief 2026-09-30", "c0", "## Mail\n- 09:12 Carol <carol@example.org> — Invoice 2026-41 — asks for the date", "alice@acme.example");

    private static string ReplyOk() => Draft("d1", "RE: Invoice 2026-41", "c1", "Dear Carol, I will call tomorrow.", "carol@example.org");

    // The bats draft_json: one Graph message of the Drafts folder.
    private static string Draft(string id, string subject, string conversation, string body, string to, string cc = "")
    {
        static JsonArray Recipients(string list) =>
        [
            .. list.Length == 0 ? [] : list.Split(',').Select(a => (JsonNode)new JsonObject { ["emailAddress"] = new JsonObject { ["name"] = "N", ["address"] = a } }),
        ];

        return new JsonObject
        {
            ["id"] = id,
            ["subject"] = subject,
            ["toRecipients"] = Recipients(to),
            ["ccRecipients"] = Recipients(cc),
            ["bccRecipients"] = new JsonArray(),
            ["conversationId"] = conversation,
            ["createdDateTime"] = "2026-09-30T05:30:00Z",
            ["changeKey"] = "CK",
            ["isDraft"] = true,
            ["body"] = new JsonObject { ["contentType"] = "text", ["content"] = body },
        }.ToJsonString();
    }

    private void Replied(string id)
    {
        Directory.CreateDirectory(StateDirectory);
        File.AppendAllText(Path.Combine(StateDirectory, "replied-2026-09-30.ids"), id + "\n", new UTF8Encoding(false));
    }

    private DraftAudit Audit()
    {
        var env = new Dictionary<string, string?> { ["ZYGGY_STATE_DIR"] = _stateRoot, ["HOME"] = _stateRoot };
        var paths = new M365Paths(env);
        var patterns = SecretPatterns.Load(Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt")).Patterns!;
        return new DraftAudit(_graph.Reader, new M365State(paths, _graph.Clock), paths, _graph.Configuration, patterns);
    }
}
