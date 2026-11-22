using System.Globalization;
using System.Text.Json;

using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Outlook;
using Zyggy.Core.Tests.Infrastructure;

using static Zyggy.Core.Tests.Outlook.OutlookTestData;

namespace Zyggy.Core.Tests.Outlook;

/// <summary>
/// AC-5 and AC-6 (spec 58 slice 2): <c>zyggy outlook policy | count | list | read</c> over the fake Outlook access — the policy record is
/// read from the laptop's <c>work-boundary.json</c> copy, else the <c>node.json</c> mirror; a refused tool prints its reason and touches
/// nothing; the read tools print JSON with invariant dates; Outlook's absence and its security prompt are exit 6 lines.
/// </summary>
public sealed class OutlookVerbTests : IDisposable
{
    private const string Record = """{ "date": "2026-11-21", "scope": ["read", "send", "move"] }""";

    private static readonly DateTimeOffset T = new(2026, 11, 22, 9, 0, 0, TimeSpan.FromHours(1));

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, NewLine = "\n" };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "zyggy-ut", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(T);
    private readonly FakeOutlookAccess _access = new();

    public OutlookVerbTests() => Directory.CreateDirectory(_root);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string NodeDir => Path.Combine(_root, "node");

    private string Config => Path.Combine(_root, "node.json");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // ---- the policy record (AC-5) ----

    [Theory]
    [InlineData("count")]
    [InlineData("list")]
    [InlineData("read m1")]
    public async Task ReadTool_NoRecord_ExitFive_PolicyNotConfirmed_TouchesNothing(string call)
    {
        // Arrange
        Mail();
        WriteNodeJson("""{ "enabled": true }""");

        // Act
        var (exit, console) = await RunAsync(call);

        // Assert
        (exit, console.Stderr, console.Stdout, _access.Calls.Count).Should().Be((5, "outlook: refused: policy not confirmed\n", string.Empty, 0));
    }

    [Theory]
    [InlineData("count")]
    [InlineData("list")]
    [InlineData("read m1")]
    public async Task ReadTool_NotEnabled_ExitFive_NotEnabled_TouchesNothing(string call)
    {
        // Arrange
        Mail();
        WriteNodeJson("""{ "enabled": false, "policyConfirmed": """ + Record + " }");
        WriteWorkBoundaryCopy(Record);

        // Act
        var (exit, console) = await RunAsync(call);

        // Assert
        (exit, console.Stderr, _access.Calls.Count).Should().Be((5, "outlook: refused: outlook not enabled\n", 0));
    }

    [Fact]
    public async Task Count_MirrorOnly_UsesTheMirror()
    {
        // Arrange
        Mail();
        WriteNodeJson("""{ "enabled": true, "policyConfirmed": """ + Record + " }");

        // Act
        var (exit, console) = await RunAsync("count");

        // Assert
        (exit, console.Stdout).Should().Be((0, Json("""{"folders":[{"folder":"Inbox","count":2},{"folder":"Drafts","count":1}]}""")));
    }

    [Fact]
    public async Task Count_CopiedFileWithoutRecordAndMirror_TheFileWins_Refused()
    {
        // Arrange
        Mail();
        WriteNodeJson("""{ "enabled": true, "policyConfirmed": """ + Record + " }");
        WriteWorkBoundaryCopy(null);

        // Act
        var (exit, console) = await RunAsync("count");

        // Assert
        (exit, console.Stderr, _access.Calls.Count).Should().Be((5, "outlook: refused: policy not confirmed\n", 0));
    }

    [Fact]
    public async Task Policy_CopiedFile_NamesTheFileItsDateScopeAndEnabled()
    {
        // Arrange
        WriteNodeJson("""{ "enabled": true }""");
        WriteWorkBoundaryCopy(Record);

        // Act
        var (exit, console) = await RunAsync("policy");

        // Assert
        (exit, console.Stdout, _access.Calls.Count).Should().Be((0,
            "policy: work-boundary.json copy at " + Path.Combine(NodeDir, "work-boundary.json") + "\n" +
            "date: 2026-11-21\n" +
            "scope: read, send, move\n" +
            "enabled: true\n" +
            "read: allowed\n",
            0));
    }

    [Fact]
    public async Task Policy_MirrorOnly_NamesTheMirror()
    {
        // Arrange
        WriteNodeJson("""{ "enabled": true, "policyConfirmed": { "date": "2026-11-20", "scope": ["read"] } }""");

        // Act
        var (exit, console) = await RunAsync("policy");

        // Assert
        (exit, console.Stdout).Should().Be((0,
            "policy: node.json mirror (outlook.policyConfirmed)\n" +
            "date: 2026-11-20\n" +
            "scope: read\n" +
            "enabled: true\n" +
            "read: allowed\n"));
    }

    [Fact]
    public async Task Policy_NoRecordNotEnabled_SaysNoneAndTheRefusal()
    {
        // Arrange
        WriteNodeJson(null);

        // Act
        var (exit, console) = await RunAsync("policy");

        // Assert
        (exit, console.Stdout).Should().Be((0,
            "policy: none (no work-boundary.json copy at " + Path.Combine(NodeDir, "work-boundary.json") + ", no outlook.policyConfirmed in node.json)\n" +
            "enabled: false\n" +
            "read: refused (outlook not enabled)\n"));
    }

    // ---- the read tools (AC-6 shape, fake access) ----

    [Fact]
    public async Task Count_NamedFolders_AddsThemAfterInboxAndDrafts()
    {
        // Arrange
        Mail();
        _access.AddFolder(FakeOutlookAccess.DefaultStore, "Inbox/Projects");
        Allowed();

        // Act
        var (exit, console) = await RunAsync("count --folder Inbox/Projects");

        // Assert
        (exit, console.Stdout).Should().Be((0, Json("""{"folders":[{"folder":"Inbox","count":2},{"folder":"Drafts","count":1},{"folder":"Inbox/Projects","count":0}]}""")));
    }

    [Fact]
    public async Task Count_UnknownFolder_ExitOne_FolderNotFound()
    {
        // Arrange
        Mail();
        Allowed();

        // Act
        var (exit, console) = await RunAsync("count --folder Nope");

        // Assert
        (exit, console.Stderr).Should().Be((1, "outlook: folder not found\n"));
    }

    [Fact]
    public async Task List_Default_InboxLastSevenDaysNewestFirst_InvariantDates()
    {
        // Arrange
        Mail();
        Allowed();

        // Act
        var (exit, console) = await RunAsync("list");

        // Assert
        (exit, console.Stdout).Should().Be((0, Json("""
            {
              "folder": "Inbox",
              "since": "2026-11-15T09:00:00+01:00",
              "items": [
                { "reference": "m2", "senderName": "Sender m2", "senderAddress": "m2@example.test", "subject": "Second", "received": "2026-11-21T08:05:00+01:00", "importance": "high", "flag": "flagged", "isRead": true, "hasAttachments": true, "isProtected": false },
                { "reference": "m1", "senderName": "Sender m1", "senderAddress": "m1@example.test", "subject": "First", "received": "2026-11-20T14:30:00+01:00", "importance": "normal", "flag": "none", "isRead": false, "hasAttachments": false, "isProtected": false }
              ]
            }
            """)));
    }

    [Fact]
    public async Task List_SinceAndMax_AreApplied()
    {
        // Arrange
        Mail();
        Allowed();

        // Act
        var (exit, console) = await RunAsync("list --since 2026-11-21 --max 1");

        // Assert
        exit.Should().Be(0);
        References(console.Stdout).Should().Equal("m2");
    }

    [Fact]
    public async Task List_SinceBeforeEverything_IsCappedByMax()
    {
        // Arrange
        Mail();
        Allowed();

        // Act
        var (_, console) = await RunAsync("list --since 2026-01-01T00:00:00Z --max 1");

        // Assert
        References(console.Stdout).Should().Equal("m2");
    }

    [Theory]
    [InlineData("list --since 21/11/2026", "--since")]
    [InlineData("list --max 0", "--max")]
    [InlineData("list --max many", "--max")]
    [InlineData("read", "read needs <reference>")]
    [InlineData("count --folder", "--folder")]
    [InlineData("frobnicate", "unknown verb")]
    [InlineData("", "no verb given")]
    public async Task BadArguments_ExitTwo_UsageLine(string call, string expectedWord)
    {
        // Arrange
        Mail();
        Allowed();

        // Act
        var (exit, console) = await RunAsync(call);

        // Assert
        (exit, console.Stderr.StartsWith("outlook: ", StringComparison.Ordinal), console.Stderr.Contains(expectedWord, StringComparison.Ordinal), _access.Calls.Count)
            .Should().Be((2, true, true, 0));
    }

    [Fact]
    public async Task Read_Item_PrintsFieldsAttachmentNamesAndTheBody()
    {
        // Arrange
        Mail();
        Allowed();

        // Act
        var (exit, console) = await RunAsync("read m2");

        // Assert
        (exit, console.Stdout).Should().Be((0, Json("""
            {
              "reference": "m2",
              "senderName": "Sender m2",
              "senderAddress": "m2@example.test",
              "subject": "Second",
              "received": "2026-11-21T08:05:00+01:00",
              "importance": "high",
              "flag": "flagged",
              "isRead": true,
              "hasAttachments": true,
              "isProtected": false,
              "attachments": ["plan.pdf"],
              "attachmentCount": 1,
              "body": "Hello",
              "bodyTruncated": false
            }
            """)));
    }

    [Fact]
    public async Task Read_ProtectedItem_BodyNull_NeverAskedOfOutlook()
    {
        // Arrange
        Mail();
        _access.Add(FakeOutlookAccess.DefaultStore, "Inbox", FakeOutlookAccess.Item("m3", Local(2026, 11, 21, 9), isProtected: true), body: "secret");
        Allowed();

        // Act
        var (exit, console) = await RunAsync("read m3");

        // Assert
        exit.Should().Be(0);
        using var document = JsonDocument.Parse(console.Stdout);
        (document.RootElement.GetProperty("isProtected").GetBoolean(), document.RootElement.GetProperty("body").ValueKind, _access.Calls.Any(c => c.StartsWith("ReadBody", StringComparison.Ordinal)))
            .Should().Be((true, JsonValueKind.Null, false));
    }

    [Fact]
    public async Task Read_UnknownReference_ExitOne_ItemNotFound()
    {
        // Arrange
        Mail();
        Allowed();

        // Act
        var (exit, console) = await RunAsync("read nope");

        // Assert
        (exit, console.Stderr).Should().Be((1, "outlook: item not found\n"));
    }

    // ---- Outlook itself ----

    [Fact]
    public async Task Count_OutlookUnavailable_ExitSix_NamesTheReason()
    {
        // Arrange
        Mail();
        Allowed();
        _access.Unavailable = true;

        // Act
        var (exit, console) = await RunAsync("count");

        // Assert
        (exit, console.Stderr).Should().Be((6, "outlook: Outlook not available (Outlook is not running)\n"));
    }

    [Fact]
    public async Task Count_OutlookPromptNotAnswered_ExitSix_SaysOutlookAskedForAccess()
    {
        // Arrange
        Allowed();
        var hanging = new HangingOutlookAccess(TimeSpan.FromSeconds(42));

        // Act
        var (exit, console) = await RunAsync("count", hanging);

        // Assert
        (exit, console.Stderr).Should().Be((6, "outlook: Outlook asked for access: no answer within 42 s; answer the prompt in Outlook or stop\n"));
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Count_OutlookRefusesAccessOnTheComThread_ExitSix_NoCrash()
    {
        // Arrange
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the COM access runs on an STA thread, Windows only");
        Allowed();
        var denied = new OutlookComAccess(TimeSpan.FromSeconds(30), () => throw new UnauthorizedAccessException("Access is denied"));

        // Act
        var (exit, console) = await RunAsync("count", denied);

        // Assert
        (exit, console.Stderr).Should().Be((6, "outlook: Outlook not available (UnauthorizedAccessException: Access is denied)\n"));
    }

    [Fact]
    public async Task Count_NoNodeJson_ExitThree_ConfigurationError()
    {
        // Act
        var (exit, console) = await RunAsync("count");

        // Assert
        (exit, console.Stderr.StartsWith("outlook: configuration error: ", StringComparison.Ordinal), _access.Calls.Count).Should().Be((3, true, 0));
    }

    // ---- helpers ----

    private void Mail()
    {
        _access
            .Add(FakeOutlookAccess.DefaultStore, "Inbox", FakeOutlookAccess.Item("m1", Local(2026, 11, 20, 14, 30), subject: "First"), body: "Body one")
            .Add(FakeOutlookAccess.DefaultStore, "Inbox", FakeOutlookAccess.Item("m2", Local(2026, 11, 21, 8, 5), subject: "Second", importance: 2, flag: 2, unread: false, attachments: ["plan.pdf"]), body: "Hello")
            .Add(FakeOutlookAccess.DefaultStore, "Drafts", FakeOutlookAccess.Item("d1", Local(2026, 11, 21, 10), subject: "Draft"))
            .Add(FakeOutlookAccess.SharedStore, "Inbox", FakeOutlookAccess.Item("s1", Local(2026, 11, 21, 11), subject: "Shared"));
    }

    private void Allowed()
    {
        WriteNodeJson("""{ "enabled": true }""");
        WriteWorkBoundaryCopy(Record);
    }

    private void WriteNodeJson(string? outlookSection)
    {
        string outlook = outlookSection is null ? string.Empty : "  \"outlook\": " + outlookSection + ",\n";
        string json = string.Create(CultureInfo.InvariantCulture, $$"""
            {
              "tenant": "acme",
              "machine": "work-laptop",
              "nodeDir": "{{NodeDir.Replace('\\', '/')}}",
            {{outlook}}  "bus": { "owner": "acme-org", "repo": "acme-bus", "localPath": "{{_root.Replace('\\', '/')}}/bus", "remote": "https://example.invalid/acme-org/acme-bus.git" },
              "secrets": { "store": "file", "root": "{{_root.Replace('\\', '/')}}/secrets" }
            }
            """);
        File.WriteAllText(Config, json);
    }

    private void WriteWorkBoundaryCopy(string? record)
    {
        Directory.CreateDirectory(NodeDir);
        string mail = record is null ? string.Empty : ", \"mail\": { \"policyConfirmed\": " + record + " }";
        File.WriteAllText(Path.Combine(NodeDir, "work-boundary.json"), "{ \"neverList\": [\"national register numbers\"]" + mail + " }");
    }

    private async Task<(int Exit, VerbConsole Console)> RunAsync(string call, IOutlookAccess? access = null)
    {
        var console = new VerbConsole();
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["ZYGGY_NODE_JSON"] = Config };
        var host = new OutlookVerbHost(environment, _clock, Zone, () => access ?? _access);
        string[] args = call.Length == 0 ? [] : call.Split(' ');
        int exit = await host.RunAsync(args, console.Io, Ct);
        return (exit, console);
    }

    private static string Json(string compactOrIndented)
    {
        using var document = JsonDocument.Parse(compactOrIndented);
        return JsonSerializer.Serialize(document.RootElement, Pretty) + "\n";
    }

    private static List<string> References(string stdout)
    {
        using var document = JsonDocument.Parse(stdout);
        return document.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("reference").GetString()!).ToList();
    }

    /// <summary>An access whose every call reports Outlook's unanswered security prompt, as the COM access does after its timeout.</summary>
    private sealed class HangingOutlookAccess(TimeSpan waited) : IOutlookAccess
    {
        public string DefaultStoreId() => throw new OutlookPromptTimeoutException(waited);

        public IEnumerable<OutlookRawItem>? Items(string storeId, string folder, DateTime sinceLocal, int max, DateTime? beforeLocal = null) => throw new OutlookPromptTimeoutException(waited);

        public int? Count(string storeId, string folder) => throw new OutlookPromptTimeoutException(waited);

        public OutlookRawItem? Find(string storeId, string entryId) => throw new OutlookPromptTimeoutException(waited);

        public string ReadBody(string storeId, string entryId) => throw new OutlookPromptTimeoutException(waited);

        public void Send(OutlookOutgoing message) => throw new OutlookPromptTimeoutException(waited);

        public bool Move(string storeId, string entryId, string folder) => throw new OutlookPromptTimeoutException(waited);
    }
}
