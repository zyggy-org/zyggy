using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Brief;

/// <summary>
/// Spec 35 AC-32..AC-39 (I, in process): one weekday <c>zyggy m365 brief</c> makes two model calls through the real process runner and
/// <c>tools/fake-claude</c> — the mail run, then the ideas run from an empty directory under the brief state with Read, Grep and Glob only
/// and the memory as an added directory — and writes a brief with both parts; a weekend run makes one call and touches no mailbox state; a
/// failing ideas run leaves the mail part intact.
/// </summary>
public sealed class BriefTwoRunsEndToEndTests : IDisposable
{
    private const string DraftsRoute = "mailFolders/drafts/messages\\?\\$filter";
    private const string ReplyDraft = """{"id":"d1","subject":"RE: Invoice 2026-41","toRecipients":[{"emailAddress":{"name":"Carol","address":"carol@example.org"}}],"ccRecipients":[],"bccRecipients":[],"conversationId":"c1","createdDateTime":"2026-10-06T08:31:00Z","changeKey":"CK1","isDraft":true,"body":{"contentType":"text","content":"Dear Carol, I will call tomorrow.\n"}}""";

    private static readonly DateTimeOffset Tuesday = new(2026, 10, 6, 8, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Saturday = new(2026, 10, 10, 8, 30, 0, TimeSpan.Zero);

    private readonly M365RunHarness _run = new();

    public BriefTwoRunsEndToEndTests()
    {
        File.WriteAllText(Path.Combine(_run.State, "mail-watermark"), "2026-10-06T04:30:00Z\n");
        File.WriteAllText(Path.Combine(_run.State, "replied-2026-10-06.ids"), "m1\n");
        File.WriteAllText(Path.Combine(_run.State, "brief-2026-10-05.json"), """{"date":"2026-10-05","window_start":"2026-10-05T04:30:00Z","drafts":[{"id":"d5","kind":"reply","subject":"RE: Old thread","recipients":[]}],"replied_ids":[],"audit":"ok","reasons":[]}""");
        _run.Graph.Once("GET", DraftsRoute, 200, "{\"value\":[" + ReplyDraft + "]}");
        Memory("business/clients/acme-corp.md", "# Acme Corp\n- [observed] 2026-09-20: Acme renewal talks start in November\n");
        Memory("private/family/holidays.md", "- [stated] 2026-09-01: school autumn holiday from 26 October to 1 November\n");
    }

    private string Principal => Path.Combine(_run.Fixture.MemoryRoot, "acme", "alice");

    private string BriefDirectory => Path.Combine(_run.Fixture.StateDirectory, "brief");

    public void Dispose() => _run.Dispose();

    private void Memory(string relative, string content)
    {
        var path = Path.Combine(Principal, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private ActingModelRunner Model(string ideas, string mail = "m365-brief-mail-ok") => _run.Model(call => call == 0 ? mail : ideas);

    private Task<(int Exit, VerbConsole Console)> BriefAsync(ActingModelRunner model, DateTimeOffset at) =>
        _run.RunAsync(model, ["brief"], TestContext.Current.CancellationToken, new FakeTimeProvider(at));

    private JsonElement LastRow() => JsonDocument.Parse(File.ReadAllLines(Path.Combine(_run.State, "brief.jsonl"))[^1]).RootElement;

    [Fact]
    public async Task Weekday_TwoCalls_MailThenIdeas_BriefHasBothParts_ShownRows()
    {
        // Arrange
        var model = Model("brief-ideas-ok");

        // Act
        var (exit, console) = await BriefAsync(model, Tuesday);

        // Assert
        exit.Should().Be(0, console.Stderr + console.Stdout);
        model.Calls.Should().Be(2);
        model.Requests[0].Tools.Should().BeNull("the mail run uses the m365 server");
        model.Requests[1].Tools.Should().Equal("Read", "Grep", "Glob");
        console.Stdout.Should().EndWith("brief 2026-10-06: mail 4 (1 urgent, 3 important, 0 other), files 1, replies 1, z 3, you 1, ideas 2, facts 2, turns 12, cost 0.42, audit ok, exit 0\n");
        var markdown = File.ReadAllText(Path.Combine(BriefDirectory, "brief-2026-10-06.md"));
        markdown.Should().StartWith("Zyggy — morning brief 2026-10-06 (4 new mails").And.Contain("## Mail\n").And.Contain("Z1. ");
        markdown.Should().EndWith(
            "## For the long run\n" +
            "1. Prepare the Acme renewal offer before the November talks — The renewal talks start in November — client → I can prepare: an outline of the offer   (basis: business/clients/acme-corp.md, \"Acme renewal talks start in November\")\n" +
            "2. Plan the autumn-holiday trip — The school holiday starts on 26 October — family → you   (basis: private/family/holidays.md, \"school autumn holiday from 26 October to 1 November\")\n");
        File.ReadAllLines(Path.Combine(BriefDirectory, "ideas.jsonl")).Should().HaveCount(2);
        var row = LastRow();
        row.GetProperty("ideas").GetInt32().Should().Be(2);
        row.GetProperty("ideas_dropped").GetInt32().Should().Be(1, "the career idea's basis is invented");
        row.GetProperty("ideas_exit").GetInt32().Should().Be(0);
        row.GetProperty("ideas_cost").GetRawText().Should().Be("0.20");
    }

    [Fact]
    public async Task Weekday_IdeasCall_CapturedArgsEqualGolden_CwdUnderBriefRunsRemovedAfter()
    {
        // Arrange
        var model = Model("brief-ideas-ok");
        var prompts = new Core.Brief.BriefPrompts();

        // Act
        var (exit, console) = await BriefAsync(model, Tuesday);

        // Assert
        exit.Should().Be(0, console.Stderr + console.Stdout);
        var expected = File.ReadAllLines(M365InstanceFixture.Golden("brief", "ideas-args.txt"))
            .Select(l => l
                .Replace("<principal-rule>", Principal.Replace('\\', '/').TrimStart('/'), StringComparison.Ordinal)
                .Replace("<principal>", Principal, StringComparison.Ordinal)
                .Replace("<schema>", prompts.IdeasSchema, StringComparison.Ordinal)
                .Replace("<prompt>", prompts.IdeasPrompt, StringComparison.Ordinal));
        var capture = FakeClaude.ReadCapture(model.ArgumentsCapture(1));
        capture.Arguments.Should().Equal(expected);
        var cwd = Path.GetFullPath(capture.WorkingDirectory);
        Path.GetDirectoryName(cwd).Should().Be(Path.GetFullPath(Path.Combine(BriefDirectory, "runs")));
        Directory.Exists(cwd).Should().BeFalse("the ideas run's directory is removed after the run");
    }

    [Fact]
    public async Task Weekday_IdeasCall_StdinHasNoMailSubject()
    {
        // Arrange: the stub's Inbox holds "Invoice 2026-41 due 2026-10-10" from Carol Example — the canary
        var model = Model("brief-ideas-ok");

        // Act
        var (exit, console) = await BriefAsync(model, Tuesday);

        // Assert
        exit.Should().Be(0, console.Stderr + console.Stdout);
        var mailStdin = Encoding.UTF8.GetString(File.ReadAllBytes(model.StdinCapture(0)));
        var ideasStdin = Encoding.UTF8.GetString(File.ReadAllBytes(model.StdinCapture(1)));
        mailStdin.Should().StartWith("/morning-brief ");
        ideasStdin.Should().StartWith("Today: 2026-10-06 (Tuesday, weekday)\n");
        ideasStdin.Should().NotContainAny("Invoice", "Carol", "Lunch on Thursday", "Q3 report", "Team offsite", "alice@acme.example");
    }

    [Fact]
    public async Task Weekday_IdeasError_NoteRowIdeasExit_ExitZero()
    {
        // Arrange
        var model = Model("brief-ideas-error");

        // Act
        var (exit, console) = await BriefAsync(model, Tuesday);

        // Assert
        exit.Should().Be(0, console.Stderr + console.Stdout);
        console.Stdout.Should().EndWith(", ideas 0, facts 2, turns 12, cost 0.42, audit ok, exit 0\n");
        File.ReadAllText(Path.Combine(BriefDirectory, "brief-2026-10-06.md")).Should().EndWith(
            "## For the long run\n- not available today (claude run failed (error_during_execution)) — runbook 13 \"Ideas run failed but mail run succeeded\"\n");
        LastRow().GetProperty("ideas_exit").GetInt32().Should().Be(6);
        File.ReadAllText(Path.Combine(_run.State, "mail-watermark")).TrimEnd('\n').Should().Be("2026-10-06T08:15:00Z", "the mail part is intact");
    }

    [Fact]
    public async Task Weekday_FabricatedBasis_Dropped_IdeasDroppedCounted()
    {
        // Arrange
        var model = Model("brief-ideas-fabricated-basis");

        // Act
        var (exit, console) = await BriefAsync(model, Tuesday);

        // Assert
        exit.Should().Be(0, console.Stderr + console.Stdout);
        File.ReadAllText(Path.Combine(BriefDirectory, "brief-2026-10-06.md")).Should().EndWith("## For the long run\n- none\n").And.NotContain("ski trip");
        LastRow().GetProperty("ideas_dropped").GetInt32().Should().Be(1);
        File.Exists(Path.Combine(BriefDirectory, "ideas.jsonl")).Should().BeFalse("nothing was shown");
    }

    [Fact]
    public async Task Weekend_OneCall_NoGraphRequest_NoReceiptWatermarkUnchanged()
    {
        // Arrange: on a weekend the first (and only) call is the ideas run
        var model = _run.Model(_ => "brief-ideas-ok");

        // Act
        var (exit, console) = await BriefAsync(model, Saturday);

        // Assert
        exit.Should().Be(0, console.Stderr + console.Stdout);
        console.Stdout.Should().Be("brief 2026-10-10: weekend, ideas 1, turns 5, cost 0.20, exit 0\n");
        model.Calls.Should().Be(1);
        model.Requests[0].Tools.Should().Equal("Read", "Grep", "Glob");
        _run.Graph.Requests.Should().BeEmpty("a weekend run makes no Graph call, not even a sign-in");
        Encoding.UTF8.GetString(File.ReadAllBytes(model.StdinCapture(0))).Should().Contain("Allowed areas: family, travel, home, hobbies\n");
        File.ReadAllText(Path.Combine(BriefDirectory, "brief-2026-10-10.md")).Should().Be(
            "Zyggy — morning brief 2026-10-10 (weekend)\n\n## For the long run\n" +
            "1. Plan the autumn-holiday trip — The school holiday starts on 26 October — family → you   (basis: private/family/holidays.md, \"school autumn holiday from 26 October to 1 November\")\n");
        File.Exists(Path.Combine(_run.State, "brief-2026-10-10.json")).Should().BeFalse("no m365 receipt on a weekend");
        File.ReadAllText(Path.Combine(_run.State, "mail-watermark")).TrimEnd('\n').Should().Be("2026-10-06T04:30:00Z");
        _run.RunDirectories.Should().BeEmpty("no mail run directory");
    }

    [Fact]
    public async Task Weekend_IdeasError_ExitSixNoFile()
    {
        // Arrange
        var model = _run.Model(_ => "brief-ideas-error");

        // Act
        var (exit, console) = await BriefAsync(model, Saturday);

        // Assert
        exit.Should().Be(6);
        console.Stderr.Should().Be("m365-brief: ideas run failed (claude run failed (error_during_execution)) — runbook 13 \"Brief run failed\"\n");
        File.Exists(Path.Combine(BriefDirectory, "brief-2026-10-10.md")).Should().BeFalse();
        File.Exists(Path.Combine(BriefDirectory, "brief-2026-10-10.json")).Should().BeFalse();
        LastRow().GetProperty("exit").GetInt32().Should().Be(6);
    }
}
