using System.Globalization;

using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;
using Zyggy.Core.Outlook;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Core.Verbs;

using static Zyggy.Core.Tests.Outlook.OutlookTestData;

namespace Zyggy.Core.Tests.Outlook;

/// <summary>
/// AC-8 and AC-9 (spec 58 slice 4) over the fake Outlook access and a fake context sender: <c>zyggy outlook share</c> takes the
/// session's facts per briefed mail on stdin, re-reads each mail, runs the sensitive-data filter on the laptop and hands only what
/// passes to the context path as <c>[observed]</c> lines with a <c>work-mail</c> provenance. A mail on the never-list or in doubt
/// yields no line and is counted on the laptop; the count crosses once, the next day, as the summary text of one context line.
/// </summary>
public sealed class OutlookShareTests : IDisposable
{
    private const string Record = """{ "date": "2026-11-21", "scope": ["read", "send", "move"] }""";

    private static readonly DateTimeOffset T = new(2026, 11, 22, 9, 0, 0, TimeSpan.FromHours(1));

    private readonly string _root = Path.Combine(Path.GetTempPath(), "zyggy-ut", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(T);
    private readonly FakeOutlookAccess _access = new();
    private readonly List<ContextRequest> _sent = [];
    private int _senderExit;

    public OutlookShareTests() => Directory.CreateDirectory(_root);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string NodeDir => Path.Combine(_root, "node");

    private string Config => Path.Combine(_root, "node.json");

    private string KeptFile => Path.Combine(NodeDir, "work", "acme", "outlook", "kept.json");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Share_BusinessMail_OneContextWithWorkMailLines()
    {
        // Arrange
        Mail();
        Allowed();

        // Act
        var (exit, _) = await RunAsync(Share(("m1", false, ["Asks to confirm the Q4 budget", "Body: Can you confirm the budget by Friday?"])));

        // Assert
        (exit, Lines()).Should().Be((0, string.Join('|',
            "- [observed] 2026-11-22 [work-mail 2026-11-20]: Asks to confirm the Q4 budget",
            "- [observed] 2026-11-22 [work-mail 2026-11-20]: Body: Can you confirm the budget by Friday?")));
    }

    [Fact]
    public async Task Share_Says_HowManyLinesAndMailsAndWhatStayed()
    {
        // Arrange
        Mail();
        Allowed();

        // Act
        var (_, console) = await RunAsync(Share(("m1", false, ["Asks to confirm the Q4 budget"]), ("n1", false, ["Asks for a call"])));

        // Assert
        console.Stdout.Should().StartWith("outlook: sharing 1 lines from 1 mails; 1 mails kept on this laptop today\n");
    }

    [Theory]
    [InlineData("n1")]
    [InlineData("t1")]
    [InlineData("p1")]
    public async Task Share_NeverListOrProtectedMail_NoLineNoContext_Counted(string reference)
    {
        // Arrange
        Mail();
        Allowed();

        // Act
        var (exit, _) = await RunAsync(Share((reference, false, ["Asks for a call"])));

        // Assert
        (exit, _sent.Count, File.ReadAllText(KeptFile).Contains("\"2026-11-22\": 1", StringComparison.Ordinal)).Should().Be((0, 0, true));
    }

    [Fact]
    public async Task Share_MarkedInDoubt_NoLine_Counted()
    {
        // Arrange
        Mail();
        Allowed();

        // Act
        await RunAsync(Share(("m1", true, ["Asks to confirm the Q4 budget"])));

        // Assert
        (_sent.Count, File.ReadAllText(KeptFile).Contains("\"2026-11-22\": 1", StringComparison.Ordinal)).Should().Be((0, true));
    }

    // The filter reads the whole body, not only the part cut at outlook.read.bodyMaxChars.
    [Fact]
    public async Task Share_NationalRegisterNumberBeyondTheReadCut_StillKept()
    {
        // Arrange
        _access.Add(FakeOutlookAccess.DefaultStore, "Inbox", FakeOutlookAccess.Item("l1", Local(2026, 11, 21, 9)), body: "Hello there, " + new string('x', 40) + " 85.07.30-033.28");
        Allowed(read: """, "read": { "bodyMaxChars": 10 }""");

        // Act
        await RunAsync(Share(("l1", false, ["Says hello"])));

        // Assert
        _sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Share_FactNamesAnAttachment_NoLine()
    {
        // Arrange
        Mail();
        Allowed();

        // Act
        await RunAsync(Share(("a1", false, ["Sends canary-offer.pdf for review"])));

        // Assert
        _sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Share_NextDay_CountCrossesOnceAsOneLine()
    {
        // Arrange
        Mail();
        Allowed();
        await RunAsync(Share(("n1", false, ["x"]), ("t1", false, ["y"])));
        _clock.Advance(TimeSpan.FromDays(1));

        // Act
        await RunAsync(Share());
        await RunAsync(Share());

        // Assert
        Lines().Should().Be("- [observed] 2026-11-22 [work-mail 2026-11-22]: 2 work mails kept on the laptop (never-list or in doubt)");
    }

    [Fact]
    public async Task Share_SendFails_CountStaysForTheNextShare()
    {
        // Arrange
        Mail();
        Allowed();
        await RunAsync(Share(("n1", false, ["x"])));
        _clock.Advance(TimeSpan.FromDays(1));
        _senderExit = 6;
        await RunAsync(Share());
        _senderExit = 0;
        _sent.Clear();

        // Act
        await RunAsync(Share());

        // Assert
        Lines().Should().Be("- [observed] 2026-11-22 [work-mail 2026-11-22]: 1 work mails kept on the laptop (never-list or in doubt)");
    }

    [Fact]
    public async Task Share_SenderExit_IsTheVerbsExit()
    {
        // Arrange
        Mail();
        Allowed();
        _senderExit = 7;

        // Act
        var (exit, _) = await RunAsync(Share(("m1", false, ["Asks to confirm the Q4 budget"])));

        // Assert
        exit.Should().Be(7);
    }

    [Fact]
    public async Task Share_NoTermList_RefusedExitThree_OutlookUntouched()
    {
        // Arrange
        Mail();
        Allowed(terms: null);

        // Act
        var (exit, console) = await RunAsync(Share(("m1", false, ["Asks"])));

        // Assert
        (exit, console.Stderr, _access.Calls.Count, _sent.Count).Should().Be((3, "outlook: refused: no sensitive-terms.txt in the node directory (the shared sensitive term list); nothing shared\n", 0, 0));
    }

    [Fact]
    public async Task Share_NoPolicyRecord_RefusedExitFive_NothingRead()
    {
        // Arrange
        Mail();
        WriteNodeJson("""{ "enabled": true }""");
        File.WriteAllText(Path.Combine(NodeDir, "sensitive-terms.txt"), "patient\n");

        // Act
        var (exit, _) = await RunAsync(Share(("m1", false, ["Asks"])));

        // Assert
        (exit, _access.Calls.Count, _sent.Count).Should().Be((5, 0, 0));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{ "mails": [ { "facts": ["no reference"] } ] }""")]
    [InlineData("""{ "mails": [ { "reference": "m1", "facts": "not a list" } ] }""")]
    [InlineData("""{ "mails": [ { "reference": "m1", "facts": ["  "] } ] }""")]
    public async Task Share_BadStdin_ExitTwo_NothingReadOrSent(string stdin)
    {
        // Arrange
        Mail();
        Allowed();

        // Act
        var (exit, _) = await RunAsync(stdin);

        // Assert
        (exit, _access.Calls.Count, _sent.Count).Should().Be((2, 0, 0));
    }

    [Fact]
    public async Task Share_FactLineOverFourHundredCharacters_ExitTwo_NothingSent()
    {
        // Arrange
        Mail();
        Allowed();

        // Act
        var (exit, console) = await RunAsync(Share(("m1", false, [new string('a', 400)])));

        // Assert
        (exit, console.Stderr, _sent.Count).Should().Be((2, "outlook: mail 1 fact 1 is longer than a memory line (400 characters with its prefix); split it\n", 0));
    }

    [Fact]
    public async Task Share_UnknownReference_ExitOne_NothingSent()
    {
        // Arrange
        Mail();
        Allowed();

        // Act
        var (exit, _) = await RunAsync(Share(("s1", false, ["Shared mailbox mail"])));

        // Assert
        (exit, _sent.Count).Should().Be((1, 0));
    }

    [Fact]
    public async Task Share_NoSender_ExitThree()
    {
        // Arrange
        Mail();
        Allowed();

        // Act
        var (exit, _) = await RunAsync(Share(("m1", false, ["Asks"])), withSender: false);

        // Assert
        exit.Should().Be(3);
    }

    // ---- helpers ----

    private void Mail() =>
        _access
            .Add(FakeOutlookAccess.DefaultStore, "Inbox", FakeOutlookAccess.Item("m1", Local(2026, 11, 20, 14, 30), subject: "Budget"), body: "Can you confirm the budget by Friday?")
            .Add(FakeOutlookAccess.DefaultStore, "Inbox", FakeOutlookAccess.Item("n1", Local(2026, 11, 21, 8)), body: "Please register 85.07.30-033.28 in the system.")
            .Add(FakeOutlookAccess.DefaultStore, "Inbox", FakeOutlookAccess.Item("t1", Local(2026, 11, 21, 9), subject: "Patient transfer"), body: "See below.")
            .Add(FakeOutlookAccess.DefaultStore, "Inbox", FakeOutlookAccess.Item("p1", Local(2026, 11, 21, 10), isProtected: true), body: "sealed")
            .Add(FakeOutlookAccess.DefaultStore, "Inbox", FakeOutlookAccess.Item("a1", Local(2026, 11, 21, 11), attachments: ["canary-offer.pdf"]), body: "See attached.")
            .Add(FakeOutlookAccess.SharedStore, "Inbox", FakeOutlookAccess.Item("s1", Local(2026, 11, 21, 11)), body: "shared");

    private void Allowed(string? terms = "patient\nmedisch\n", string read = "")
    {
        WriteNodeJson("{ \"enabled\": true" + read + " }");
        Directory.CreateDirectory(NodeDir);
        File.WriteAllText(Path.Combine(NodeDir, "work-boundary.json"), "{ \"mail\": { \"policyConfirmed\": " + Record + " } }");
        if (terms is not null)
        {
            File.WriteAllText(Path.Combine(NodeDir, "sensitive-terms.txt"), terms);
        }
    }

    private void WriteNodeJson(string outlookSection)
    {
        Directory.CreateDirectory(NodeDir);
        string json = string.Create(CultureInfo.InvariantCulture, $$"""
            {
              "tenant": "acme",
              "machine": "work-laptop",
              "nodeDir": "{{NodeDir.Replace('\\', '/')}}",
              "outlook": {{outlookSection}},
              "bus": { "owner": "acme-org", "repo": "acme-bus", "localPath": "{{_root.Replace('\\', '/')}}/bus", "remote": "https://example.invalid/acme-org/acme-bus.git" },
              "secrets": { "store": "file", "root": "{{_root.Replace('\\', '/')}}/secrets" }
            }
            """);
        File.WriteAllText(Config, json);
    }

    private static string Share(params (string Reference, bool Doubt, string[] Facts)[] mails) =>
        System.Text.Json.JsonSerializer.Serialize(new { mails = mails.Select(m => new { reference = m.Reference, doubt = m.Doubt, facts = m.Facts }) });

    private string Lines() => string.Join('|', _sent.SelectMany(r => r.Lines));

    private async Task<(int Exit, VerbConsole Console)> RunAsync(string stdin, bool withSender = true)
    {
        var console = new VerbConsole(stdin);
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["ZYGGY_NODE_JSON"] = Config };
        var host = new OutlookVerbHost(environment, _clock, Zone, () => _access) { ContextSender = withSender ? SendAsync : null };
        int exit = await host.RunAsync(["share"], console.Io, Ct);
        return (exit, console);
    }

    private Task<int> SendAsync(NodeConfiguration node, ContextRequest request, VerbIo io, CancellationToken ct)
    {
        _sent.Add(request);
        return Task.FromResult(_senderExit);
    }
}
