using System.Globalization;

using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Envelope;
using Zyggy.Core.Outlook;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

using static Zyggy.Core.Tests.Outlook.OutlookTestData;

namespace Zyggy.Core.Tests.Outlook;

/// <summary>
/// AC-16 and AC-9 (spec 58 slice 5) over the fake Outlook access: in a run the node started (<c>ZYGGY_JOB</c> set), the read tools
/// apply the sensitive-data filter at the source — a mail on the never-list (register number, term, also in an attachment name) or in
/// doubt (protected) is never printed, only counted in the job's marker on the laptop; the job brief keeps its own watermark, so an
/// attended brief still shows every mail; its drafts are kept on the laptop for an attended send (D-Q17); a worktree job, a job without
/// the term list and a malformed job id read nothing.
/// </summary>
public sealed class OutlookJobRunTests : IDisposable
{
    private const string Job = "01M81N0000000000000000000A";

    private const string Record = """{ "date": "2026-11-21", "scope": ["read", "send", "move"] }""";

    private const string BusinessCanary = "CANARY-BUSINESS-41aa";

    private const string RegisterCanary = "CANARY-NRN-77b0";

    private const string TermCanary = "CANARY-TERM-3c9e";

    private const string AttachmentCanary = "CANARY-ATTACH-0d4f";

    private const string ProtectedCanary = "CANARY-SEALED-61e2";

    private static readonly DateTimeOffset T = new(2026, 11, 22, 9, 0, 0, TimeSpan.FromHours(1));

    private readonly string _root = Path.Combine(Path.GetTempPath(), "zyggy-ut", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(T);
    private readonly FakeOutlookAccess _access = new();

    public OutlookJobRunTests() => Directory.CreateDirectory(_root);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string NodeDir => Path.Combine(_root, "node");

    private string Config => Path.Combine(_root, "node.json");

    private string Marker => OutlookJobRun.MarkerPath(NodeDir, TenantId.Parse("acme"), EnvelopeId.Parse(Job));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Brief_InAJob_PrintsOnlyMailThatPassesTheFilter()
    {
        // Arrange
        Mail();
        Allowed();

        // Act
        var (exit, console) = await RunAsync(InJob(), "brief");

        // Assert
        (exit, Holds(console.Stdout + console.Stderr)).Should().Be((0, "business:True register:False term:False attachment:False sealed:False"));
    }

    [Fact]
    public async Task Brief_InAJob_SaysHowManyMailsWereKept()
    {
        // Arrange
        Mail();
        Allowed();

        // Act
        var (_, console) = await RunAsync(InJob(), "brief");

        // Assert
        console.Stdout.Should().Contain("\"kept\": 4");
    }

    [Fact]
    public async Task Brief_InAJob_KeptMailsCountedInTheJobsMarker()
    {
        // Arrange
        Mail();
        Allowed();

        // Act
        await RunAsync(InJob(), "brief");

        // Assert
        OutlookJobRun.KeptCount(Marker).Should().Be(4);
    }

    [Fact]
    public async Task BriefThenRead_InAJob_SameKeptMailCountedOnce()
    {
        // Arrange
        Mail();
        Allowed();
        await RunAsync(InJob(), "brief");

        // Act
        await RunAsync(InJob(), "read", "n1");

        // Assert
        OutlookJobRun.KeptCount(Marker).Should().Be(4);
    }

    [Fact]
    public async Task Brief_Attended_PrintsEveryMail_NoMarker()
    {
        // Arrange
        Mail();
        Allowed();

        // Act
        var (_, console) = await RunAsync(Attended(), "brief");

        // Assert
        (Holds(console.Stdout), File.Exists(Marker)).Should().Be(("business:True register:True term:True attachment:True sealed:True", false));
    }

    [Theory]
    [InlineData("n1")]
    [InlineData("t1")]
    [InlineData("a1")]
    [InlineData("p1")]
    public async Task Read_KeptMailInAJob_RefusedExitFive_NothingOfItPrinted(string reference)
    {
        // Arrange
        Mail();
        Allowed();

        // Act
        var (exit, console) = await RunAsync(InJob(), "read", reference);

        // Assert
        (exit, console.Stdout, console.Stderr, OutlookJobRun.KeptCount(Marker))
            .Should().Be((5, string.Empty, "outlook: refused: this mail stays on the laptop (sensitive-data filter); it is counted\n", 1));
    }

    [Fact]
    public async Task Read_BusinessMailInAJob_Printed()
    {
        // Arrange
        Mail();
        Allowed();

        // Act
        var (exit, console) = await RunAsync(InJob(), "read", "m1");

        // Assert
        (exit, console.Stdout.Contains(BusinessCanary, StringComparison.Ordinal)).Should().Be((0, true));
    }

    [Fact]
    public async Task List_InAJob_OmitsKeptMails()
    {
        // Arrange
        Mail();
        Allowed();

        // Act
        var (_, console) = await RunAsync(InJob(), "list", "--since", "2026-11-01");

        // Assert
        (console.Stdout.Contains("\"m1\"", StringComparison.Ordinal), console.Stdout.Contains("\"t1\"", StringComparison.Ordinal), console.Stdout.Contains("\"kept\": 4", StringComparison.Ordinal))
            .Should().Be((true, false, true));
    }

    [Fact]
    public async Task Count_InAJob_Allowed_NoContent()
    {
        // Arrange
        Mail();
        _access.AddFolder(FakeOutlookAccess.DefaultStore, "Drafts");
        Allowed();

        // Act
        var (exit, _) = await RunAsync(InJob(), "count");

        // Assert
        exit.Should().Be(0);
    }

    [Fact]
    public async Task Brief_InAJobWithoutTheTermList_RefusedExitThree_OutlookUntouched()
    {
        // Arrange
        Mail();
        Allowed(terms: null);

        // Act
        var (exit, console) = await RunAsync(InJob(), "brief");

        // Assert
        (exit, console.Stdout, console.Stderr, _access.Calls.Count)
            .Should().Be((3, string.Empty, "outlook: refused: no sensitive-terms.txt in the node directory (the shared sensitive term list); a job reads no work mail without it\n", 0));
    }

    [Fact]
    public async Task Brief_InAWorktreeJob_RefusedExitFive_OutlookUntouched()
    {
        // Arrange
        Mail();
        Allowed();
        Directory.CreateDirectory(Path.Combine(NodeDir, "worktrees", "acme", Job));

        // Act
        var (exit, console) = await RunAsync(InJob(), "brief");

        // Assert
        (exit, console.Stderr, _access.Calls.Count)
            .Should().Be((5, "outlook: refused: a worktree job reads no work mail (its branch leaves the laptop without the filter)\n", 0));
    }

    [Theory]
    [InlineData("../../escape")]
    [InlineData("not-a-job-id")]
    public async Task Brief_MalformedJobId_RefusedExitFive_OutlookUntouched(string job)
    {
        // Arrange
        Mail();
        Allowed();

        // Act
        var (exit, _) = await RunAsync(InJob(job), "brief");

        // Assert
        (exit, _access.Calls.Count).Should().Be((5, 0));
    }

    // Adversarial pass: a job could point the tools at another node.json (another node directory, an empty term list), so its marker
    // would sit where the runner never looks and its report would leave unscreened.
    [Fact]
    public async Task Brief_InAJobWithConfigOption_RefusedExitFive_OutlookUntouched()
    {
        // Arrange
        Mail();
        Allowed();

        // Act
        var (exit, console) = await RunAsync(InJob(), "brief", "--config", Config);

        // Assert
        (exit, console.Stderr, _access.Calls.Count).Should().Be((5, "outlook: refused: in a job the outlook tools read the node's own node.json (no --config)\n", 0));
    }

    [Fact]
    public async Task Brief_InAJobWithoutTheRunnersMarker_RefusedExitFive_OutlookUntouched()
    {
        // Arrange
        Mail();
        Allowed();
        var environment = InJob();
        environment.Remove("ZYGGY_JOB_MARKER");

        // Act
        var (exit, _) = await RunAsync(environment, "brief");

        // Assert
        (exit, _access.Calls.Count).Should().Be((5, 0));
    }

    [Fact]
    public async Task Brief_InAJobWhoseMarkerIsNotInThisNodeDirectory_RefusedExitFive_OutlookUntouched()
    {
        // Arrange
        Mail();
        Allowed();
        var environment = InJob();
        environment["ZYGGY_JOB_MARKER"] = Path.Combine(_root, "elsewhere", Job + ".json");

        // Act
        var (exit, console) = await RunAsync(environment, "brief");

        // Assert
        (exit, console.Stderr, _access.Calls.Count).Should().Be((5, "outlook: refused: this node.json is not the node's that runs the job\n", 0));
    }

    [Fact]
    public async Task Drafts_InAJob_RefusedExitFive()
    {
        // Arrange
        Mail();
        Allowed();

        // Act
        var (exit, console) = await RunAsync(InJob(), "drafts");

        // Assert
        (exit, console.Stderr).Should().Be((5, "outlook: refused: drafts are shown in an attended session on this laptop\n"));
    }

    // D-Q17: the drafts of a job-run brief are kept on the laptop; an attended session shows them by number.
    [Fact]
    public async Task BriefDone_InAJob_DraftsShownInAnAttendedSession()
    {
        // Arrange
        Mail();
        Allowed();
        var (_, brief) = await RunAsync(InJob(), "brief");
        await RunWithStdinAsync(InJob(), """{ "drafts": [ { "reference": "m1", "text": "Thanks, I confirm the budget." } ] }""", "brief-done", "--until", Until(brief.Stdout));

        // Act
        var (exit, console) = await RunAsync(Attended(), "drafts", "1");

        // Assert
        (exit, console.Stdout.Contains("Thanks, I confirm the budget.", StringComparison.Ordinal)).Should().Be((0, true));
    }

    // The job brief keeps its own watermark: an attended brief afterwards still shows every mail, kept ones included.
    [Fact]
    public async Task BriefDone_InAJob_AttendedBriefStillShowsEveryMail()
    {
        // Arrange
        Mail();
        Allowed();
        var (_, brief) = await RunAsync(InJob(), "brief");
        await RunAsync(InJob(), "brief-done", "--until", Until(brief.Stdout));

        // Act
        var (_, console) = await RunAsync(Attended(), "brief");

        // Assert
        Holds(console.Stdout).Should().Be("business:True register:True term:True attachment:True sealed:True");
    }

    // The next job brief starts after the last recorded job brief.
    [Fact]
    public async Task BriefDone_InAJob_NextJobBriefStartsAfterIt()
    {
        // Arrange
        Mail();
        Allowed();
        var (_, brief) = await RunAsync(InJob(), "brief");
        await RunAsync(InJob(), "brief-done", "--until", Until(brief.Stdout));

        // Act
        var (_, console) = await RunAsync(InJob(), "brief");

        // Assert
        console.Stdout.Contains(BusinessCanary, StringComparison.Ordinal).Should().BeFalse();
    }

    // ---- helpers ----

    private Dictionary<string, string?> InJob(string job = Job) =>
        new(StringComparer.OrdinalIgnoreCase) { ["ZYGGY_JOB"] = job, ["ZYGGY_HOOKS"] = "off", ["ZYGGY_JOB_MARKER"] = Path.Combine(NodeDir, "work", "acme", "outlook", "jobs", job + ".json") };

    private static Dictionary<string, string?> Attended() => new(StringComparer.OrdinalIgnoreCase);

    private static string Until(string stdout)
    {
        using var document = System.Text.Json.JsonDocument.Parse(stdout);
        return document.RootElement.GetProperty("until").GetString()!;
    }

    private static string Holds(string text) =>
        $"business:{text.Contains(BusinessCanary, StringComparison.Ordinal)} register:{text.Contains(RegisterCanary, StringComparison.Ordinal)} "
        + $"term:{text.Contains(TermCanary, StringComparison.Ordinal)} attachment:{text.Contains(AttachmentCanary, StringComparison.Ordinal)} "
        + $"sealed:{text.Contains(ProtectedCanary, StringComparison.Ordinal)}";

    private void Mail() =>
        _access
            .Add(FakeOutlookAccess.DefaultStore, "Inbox", FakeOutlookAccess.Item("m1", Local(2026, 11, 21, 14, 30), subject: "Budget"), body: BusinessCanary + " Can you confirm the budget by Friday?")
            .Add(FakeOutlookAccess.DefaultStore, "Inbox", FakeOutlookAccess.Item("n1", Local(2026, 11, 21, 8), subject: "Registration " + RegisterCanary), body: "Please register 85.07.30-033.28 in the system.")
            .Add(FakeOutlookAccess.DefaultStore, "Inbox", FakeOutlookAccess.Item("t1", Local(2026, 11, 21, 9), subject: "Transfer " + TermCanary), body: "The patient moves on Monday.")
            .Add(FakeOutlookAccess.DefaultStore, "Inbox", FakeOutlookAccess.Item("a1", Local(2026, 11, 21, 10), subject: "Report " + AttachmentCanary, attachments: ["medisch verslag.pdf"]), body: "See attached.")
            .Add(FakeOutlookAccess.DefaultStore, "Inbox", FakeOutlookAccess.Item("p1", Local(2026, 11, 21, 11), subject: "Sealed " + ProtectedCanary, isProtected: true), body: "sealed");

    private void Allowed(string? terms = "patient\nmedisch\n")
    {
        Directory.CreateDirectory(NodeDir);
        string json = string.Create(CultureInfo.InvariantCulture, $$"""
            {
              "tenant": "acme",
              "machine": "work-laptop",
              "nodeDir": "{{NodeDir.Replace('\\', '/')}}",
              "outlook": { "enabled": true },
              "bus": { "owner": "acme-org", "repo": "acme-bus", "localPath": "{{_root.Replace('\\', '/')}}/bus", "remote": "https://example.invalid/acme-org/acme-bus.git" },
              "secrets": { "store": "file", "root": "{{_root.Replace('\\', '/')}}/secrets" }
            }
            """);
        File.WriteAllText(Config, json);
        File.WriteAllText(Path.Combine(NodeDir, "work-boundary.json"), "{ \"mail\": { \"policyConfirmed\": " + Record + " } }");
        if (terms is not null)
        {
            File.WriteAllText(Path.Combine(NodeDir, "sensitive-terms.txt"), terms);
        }
    }

    private async Task<(int Exit, VerbConsole Console)> RunWithStdinAsync(Dictionary<string, string?> environment, string? stdin, params string[] args)
    {
        var console = new VerbConsole(stdin);
        environment["ZYGGY_NODE_JSON"] = Config;
        var host = new OutlookVerbHost(environment, _clock, Zone, () => _access);
        int exit = await host.RunAsync(args, console.Io, Ct);
        return (exit, console);
    }

    private Task<(int Exit, VerbConsole Console)> RunAsync(Dictionary<string, string?> environment, params string[] args) =>
        RunWithStdinAsync(environment, null, args);
}
