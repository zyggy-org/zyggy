using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Cli;

/// <summary>
/// Spec 18 slice 4 through the real verbs, real git and two checkouts of one bare repository: a session on <c>home-laptop</c> asks
/// with <c>zyggy ask</c> and gets Central's <c>zyggy decide</c> answer or its default (AC-6, AC-7), Central's decision is recorded and
/// listed (AC-8), <c>zyggy work</c> shows the session waiting (AC-5) and <c>zyggy verify</c> judges the new folders (AC-9).
/// </summary>
public sealed class AskDecideCommandTests : IAsyncLifetime
{
    private const string Session = "0b7c3d1e";

    private const string Question = "Which role name does the booking pool use?";

    private const string Default = "keep the name central";

    private const string AwsKey = "AKIAABCDEFGHIJKLMNOP";

    private const string Answer = "The spec says role central. Answered from the approved spec.";

    private static readonly DateTimeOffset T = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(T);

    private CliHarness _harness = null!;

    private string _project = null!;

    private static string QuestionFolder => BusPaths.Questions(CliHarness.Acme, CliHarness.HomeLaptop);

    private static string DecisionFolder => BusPaths.Decisions(CliHarness.Acme, CliHarness.Central);

    public async ValueTask InitializeAsync()
    {
        _harness = await CliHarness.CreateAsync(CancellationToken.None);
        _project = Path.Combine(_harness.Scratch.File("projects"), "Calizr");
        Directory.CreateDirectory(_project);
    }

    public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

    // AC-7
    [Fact]
    public async Task Ask_CentralDecidesInsideTheWait_ExitZeroWithTheDecision_QuestionArchived()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await SessionStartAsync(ct);
        Task<(int Exit, VerbConsole Console)> asking = AskAsync(["--wait", "5", "--option", "central", "--option", "hub"], ct: ct);
        string question = await QuestionOnMainAsync(ct);

        // Act
        var (decideExit, decideConsole) = await DecideAsync(IdOf(question), Answer, ct);
        var (exit, console) = await DriveAsync(asking, ct);

        // Assert
        decideExit.Should().Be(0, decideConsole.Stderr);
        exit.Should().Be(0, console.Stderr);
        console.Stdout.Should().Contain(Answer).And.Contain("answer");
        (await FilesOnMainAsync(QuestionFolder, ct)).Should().BeEmpty();
        (await FilesOnMainAsync(ArchiveOf(T), ct)).Should().ContainSingle(f => f.EndsWith(IdOf(question) + ".md", StringComparison.Ordinal));
        Model asked = await _harness.ParseOnMainAsync(BusPaths.EnvelopeFile(ArchiveOf(T), EnvelopeId.Parse(IdOf(question))), ct);
        asked.Question!.Session.Should().Be(Session);
        asked.Question.Project.Should().Be("calizr");
        asked.Question.Options.Should().Equal("central", "hub");
        asked.Question.Default.Should().Be(Default);
    }

    [Fact]
    public async Task Ask_NoDecisionInsideTheWait_ExitEightWithTheDefaultLine_QuestionStaysOpen()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await SessionStartAsync(ct);

        // Act
        var (exit, console) = await DriveAsync(AskAsync(["--wait", "1"], ct: ct), ct);

        // Assert
        exit.Should().Be(8, console.Stderr);
        console.Stdout.Should().Be("no answer; default: " + Default + "\n");
        (await FilesOnMainAsync(QuestionFolder, ct)).Should().ContainSingle();
    }

    // AC-6
    [Fact]
    public async Task Ask_TextMatchesASecretPattern_ExitFive_RemoteUnchanged()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await SessionStartAsync(ct);
        int commits = await _harness.CommitCountOnMainAsync(ct);

        // Act
        var (exit, console) = await AskAsync([], question: "Is " + AwsKey + " the right token?", ct: ct);

        // Assert
        exit.Should().Be(5);
        console.Stderr.Should().StartWith("zyggy: refused: ").And.NotContain(AwsKey);
        console.Stdout.Should().BeEmpty();
        (await _harness.CommitCountOnMainAsync(ct)).Should().Be(commits);
    }

    [Fact]
    public async Task Ask_Over600Characters_ExitFive_RemoteUnchanged()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await SessionStartAsync(ct);
        int commits = await _harness.CommitCountOnMainAsync(ct);
        string longText = string.Join(' ', Enumerable.Repeat("which role name", 41));

        // Act
        var (exit, console) = await AskAsync([], question: longText, ct: ct);

        // Assert
        longText.Length.Should().BeGreaterThan(600);
        exit.Should().Be(5, console.Stderr);
        (await _harness.CommitCountOnMainAsync(ct)).Should().Be(commits);
    }

    [Fact]
    public async Task Ask_NoLiveSessionAndNoJob_ExitTwo_NothingWritten()
    {
        // Act
        var ct = TestContext.Current.CancellationToken;
        int commits = await _harness.CommitCountOnMainAsync(ct);
        var (exit, console) = await AskAsync([], ct: ct);

        // Assert
        exit.Should().Be(2);
        console.Stderr.Should().Contain("no live session");
        (await _harness.CommitCountOnMainAsync(ct)).Should().Be(commits);
    }

    [Fact]
    public async Task Ask_FromAJob_NamesTheJob_WaitsNoLongerThanTheJobHasLeft()
    {
        // Arrange: a job run's environment whose timeout is now; no session exists
        var ct = TestContext.Current.CancellationToken;
        var job = EnvelopeId.New(T.AddMinutes(-10));
        var env = new Dictionary<string, string?>
        {
            ["ZYGGY_HOOKS"] = "off",
            ["ZYGGY_JOB"] = job.Value,
            ["ZYGGY_JOB_PROJECT"] = "calizr",
            ["ZYGGY_JOB_ENDS"] = "2026-11-15T10:00:00Z",
            ["CLAUDE_PROJECT_DIR"] = Path.Combine(_harness.Scratch.File("runs"), job.Value),
        };

        // Act: the fake clock is never moved, so the verb returns only when its wait is capped at the job's end (the default
        // wait in a job is 5 minutes); otherwise the two-minute guard in AskAsync fails the test
        var (exit, console) = await AskAsync([], env: env, ct: ct);

        // Assert
        exit.Should().Be(8, console.Stderr);
        _clock.GetUtcNow().Should().Be(T);
        Model asked = await _harness.ParseOnMainAsync((await FilesOnMainAsync(QuestionFolder, ct)).Single(), ct);
        asked.Question!.Job.Should().Be(job);
        asked.Question.Session.Should().BeNull();
        asked.Question.Project.Should().Be("calizr");
    }

    // AC-8
    [Fact]
    public async Task Decide_OnALaptop_ExitThree_NothingWritten()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string question = await AskedAndUnansweredAsync(ct);
        int commits = await _harness.CommitCountOnMainAsync(ct);

        // Act
        var (exit, console) = await _harness.RunAsync(DecideArgs(IdOf(question), Answer), _harness.HomeLaptopNodeJson, more: Clock, ct: ct);

        // Assert
        exit.Should().Be(3);
        console.Stderr.Should().Contain("only central decides");
        (await _harness.CommitCountOnMainAsync(ct)).Should().Be(commits);
    }

    [Fact]
    public async Task Decide_BodyMatchesASecretPattern_ExitFive_NothingWritten()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string question = await AskedAndUnansweredAsync(ct);
        int commits = await _harness.CommitCountOnMainAsync(ct);

        // Act
        var (exit, console) = await DecideAsync(IdOf(question), "Use " + AwsKey + " for it.", ct);

        // Assert
        exit.Should().Be(5);
        console.Stderr.Should().StartWith("zyggy: refused: ").And.NotContain(AwsKey);
        (await _harness.CommitCountOnMainAsync(ct)).Should().Be(commits);
    }

    [Fact]
    public async Task Decide_UnknownId_ExitOne_NothingWritten()
    {
        // Act
        var ct = TestContext.Current.CancellationToken;
        int commits = await _harness.CommitCountOnMainAsync(ct);
        var (exit, console) = await DecideAsync(EnvelopeId.New(T).Value, Answer, ct);

        // Assert
        exit.Should().Be(1);
        console.Stderr.Should().Contain("nothing on the bus has that id");
        (await _harness.CommitCountOnMainAsync(ct)).Should().Be(commits);
    }

    [Fact]
    public async Task Decide_WritesKindBasisEvidenceAndUndoAsGiven_ToTheAskingMachine()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string question = await AskedAndUnansweredAsync(ct);
        string report = EnvelopeId.New(T.AddMinutes(-3)).Value;

        // Act
        var (exit, console) = await _harness.RunAsync(
            ["decide", "--reply-to", IdOf(question), "--kind", "correct", "--basis", "verified", "--step", "slice 2", "--evidence", report, "--evidence", "9f2c1ab", "--undo", "revert 9f2c1ab", Answer],
            _harness.CentralNodeJson,
            more: Clock,
            ct: ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        Model decision = await _harness.ParseOnMainAsync((await FilesOnMainAsync(DecisionFolder, ct)).Single(), ct);
        decision.Header.From.Should().Be(CliHarness.Central);
        decision.Header.To.Should().Be(CliHarness.HomeLaptop);
        decision.Header.InReplyTo!.Value.Should().Be(IdOf(question));
        decision.Decision.Should().BeEquivalentTo(new DecisionFields(DecisionKind.Correct, DecisionBasis.Verified, "revert 9f2c1ab", null, "calizr", "slice 2", [report, "9f2c1ab"]));
        decision.BodyText.Should().Be(Answer + "\n");
        (await _harness.Bus.LastCommitSubjectAsync(ct)).Should().StartWith("acme decision ").And.EndWith("central→home-laptop");
    }

    [Fact]
    public async Task Decisions_ListsTheLiveAndTheArchivedDecision_NewestFirst_FilteredBySince()
    {
        // Arrange: one decision archived last month, one written now
        var ct = TestContext.Current.CancellationToken;
        DateTimeOffset lastMonth = T.AddDays(-30);
        string archived = await SeedDecisionAsync(ArchiveOf(lastMonth), lastMonth, "Archived answer of last month.", ct);
        string question = await AskedAndUnansweredAsync(ct);
        await DecideAsync(IdOf(question), Answer, ct);

        // Act
        var (exit, all) = await _harness.RunAsync(["decisions"], _harness.HomeLaptopNodeJson, more: Clock, ct: ct);
        var (_, recent) = await _harness.RunAsync(["decisions", "--since", "2026-11-01"], _harness.HomeLaptopNodeJson, more: Clock, ct: ct);

        // Assert
        exit.Should().Be(0, all.Stderr);
        all.Stdout.Should().Contain(Answer).And.Contain("Archived answer of last month.").And.Contain("undo: nothing to undo").And.Contain("answer  reported");
        all.Stdout.IndexOf(Answer, StringComparison.Ordinal).Should().BeLessThan(all.Stdout.IndexOf(IdOf(archived), StringComparison.Ordinal));
        recent.Stdout.Should().Contain(Answer).And.NotContain(IdOf(archived));
    }

    [Fact]
    public async Task SessionStart_DecisionCameAfterTheWait_PrintedOnce()
    {
        // Arrange: the wait ran out, then Central decided
        var ct = TestContext.Current.CancellationToken;
        string question = await AskedAndUnansweredAsync(ct);
        await DecideAsync(IdOf(question), Answer, ct);
        _clock.Advance(TimeSpan.FromMinutes(30));

        // Act
        var (exit, first) = await SessionStartAsync(ct);
        var (_, second) = await SessionStartAsync(ct);

        // Assert
        exit.Should().Be(0, first.Stderr);
        first.Stdout.Should().Contain(Answer).And.Contain(IdOf(question));
        second.Stdout.Should().BeEmpty();
    }

    // AC-5
    [Fact]
    public async Task Work_OpenQuestionNamesTheSession_WaitingForCentral_UntilTheDecision()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string question = await AskedAndUnansweredAsync(ct);

        // Act
        var (exit, waiting) = await WorkAsync(ct);
        await DecideAsync(IdOf(question), Answer, ct);
        var (_, answered) = await WorkAsync(ct);

        // Assert
        exit.Should().Be(0, waiting.Stderr);
        string line = waiting.Stdout.Split('\n').Should().ContainSingle(l => l.StartsWith("  session " + Session, StringComparison.Ordinal)).Subject;
        line.Should().Contain("  waiting for Central  ");
        waiting.Stdout.Should().Contain("  question " + IdOf(question) + "  calizr  session " + Session + "  asked ").And.NotContain(Question);
        answered.Stdout.Should().NotContain("waiting for Central").And.NotContain("question " + IdOf(question));
    }

    // AC-9
    [Fact]
    public async Task Verify_StatusQuestionAndDecisionPass_MisplacedOnesAreReported()
    {
        // Arrange: a live status, an open question, an answered one with its decision; then a decision in a laptop's folder and a
        // question written into another machine's folder
        var ct = TestContext.Current.CancellationToken;
        string answered = await AskedAndUnansweredAsync(ct);
        await DecideAsync(IdOf(answered), Answer, ct);
        _clock.Advance(TimeSpan.FromMinutes(1));
        string open = await AskedAndUnansweredAsync(ct);
        string decision = (await FilesOnMainAsync(DecisionFolder, ct)).Single();
        string misplacedDecision = await SeedDecisionAsync(BusPaths.Decisions(CliHarness.Acme, CliHarness.HomeLaptop), T, "In the wrong folder.", ct);
        string foreignQuestion = await SeedQuestionAsync(CliHarness.HomeLaptop, CliHarness.Central, T.AddSeconds(5), ct);
        await _harness.Bus.PullCloneAsync(ct);

        // Act
        var (_, console) = await _harness.RunAsync(["verify", "--verbose"], _harness.HomeLaptopNodeJson, ct: ct);

        // Assert
        string[] lines = console.Stdout.Split('\n');
        foreach (string good in new[] { open, decision, (await FilesOnMainAsync(BusPaths.Status(CliHarness.Acme, CliHarness.HomeLaptop), ct)).Single() })
        {
            lines.Should().Contain(l => l.StartsWith("ok", StringComparison.OrdinalIgnoreCase) && l.Contains(good, StringComparison.Ordinal));
        }

        lines.Should().ContainSingle(l => l.Contains(misplacedDecision, StringComparison.Ordinal)).Which.Should().StartWith("FAIL").And.Contain("writer_mismatch");
        lines.Should().ContainSingle(l => l.Contains(foreignQuestion, StringComparison.Ordinal)).Which.Should().StartWith("FAIL").And.Contain("writer_mismatch");
        lines.Where(l => l.StartsWith("FAIL", StringComparison.Ordinal)).Should().HaveCount(2);
    }

    private static string IdOf(string path) => Path.GetFileNameWithoutExtension(path);

    private static string ArchiveOf(DateTimeOffset at) => BusPaths.Archive(CliHarness.Acme, at.Year, at.Month);

    private static string[] DecideArgs(string replyTo, string text) =>
        ["decide", "--reply-to", replyTo, "--kind", "answer", "--basis", "reported", "--undo", "nothing to undo", text];

    private void Clock(IServiceCollection services) => services.AddSingleton<TimeProvider>(_clock);

    private Task<(int Exit, VerbConsole Console)> SessionStartAsync(CancellationToken ct) =>
        _harness.RunAsync(["work", "--session-start"], _harness.HomeLaptopNodeJson, stdin: HookJson.Stop(null, cwd: _project), more: Clock, ct: ct);

    /// <summary>Runs <c>zyggy ask</c>; a verb that waits on the fake clock when it should have refused or stopped fails after two real minutes instead of hanging.</summary>
    private Task<(int Exit, VerbConsole Console)> AskAsync(string[] options, string question = Question, IReadOnlyDictionary<string, string?>? env = null, CancellationToken ct = default) =>
        _harness.RunAsync(
            ["ask", .. options, "--default", Default, question],
            _harness.HomeLaptopNodeJson,
            extraEnvironment: env ?? new Dictionary<string, string?> { ["CLAUDE_PROJECT_DIR"] = _project },
            more: Clock,
            ct: ct).WaitAsync(TimeSpan.FromMinutes(2), ct);

    private Task<(int Exit, VerbConsole Console)> DecideAsync(string replyTo, string text, CancellationToken ct) =>
        _harness.RunAsync(DecideArgs(replyTo, text), _harness.CentralNodeJson, more: Clock, ct: ct);

    private async Task<(int Exit, VerbConsole Console)> WorkAsync(CancellationToken ct)
    {
        await _harness.Bus.RunGitAsync(_harness.Bus.SenderDir, ["pull", "--rebase", "origin", "main"], ct);
        return await _harness.RunAsync(["work"], _harness.CentralNodeJson, more: Clock, ct: ct);
    }

    /// <summary>A session that asked with a one-minute wait and got no answer; returns the question's path on the bus.</summary>
    private async Task<string> AskedAndUnansweredAsync(CancellationToken ct)
    {
        await SessionStartAsync(ct);
        var (exit, console) = await DriveAsync(AskAsync(["--wait", "1"], ct: ct), ct);
        exit.Should().Be(8, console.Stderr);
        return (await FilesOnMainAsync(QuestionFolder, ct)).OrderBy(f => f, StringComparer.Ordinal).Last();
    }

    /// <summary>
    /// Moves the fake clock in 20-second steps while the verb waits. The verb's git calls take real time, so each step is followed by a
    /// short real pause: the only real delay in these tests, bounded by the verb's own wait.
    /// </summary>
    private async Task<(int Exit, VerbConsole Console)> DriveAsync(Task<(int Exit, VerbConsole Console)> running, CancellationToken ct)
    {
        for (int step = 0; step < 400 && !running.IsCompleted; step++)
        {
            await Task.WhenAny(running, Task.Delay(TimeSpan.FromMilliseconds(250), ct));
            if (!running.IsCompleted)
            {
                _clock.Advance(TimeSpan.FromSeconds(20));
            }
        }

        running.IsCompleted.Should().BeTrue("the verb returns once its wait ran out on the fake clock");
        return await running;
    }

    /// <summary>The one question on the remote, once the asking verb pushed it.</summary>
    private async Task<string> QuestionOnMainAsync(CancellationToken ct)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            if ((await FilesOnMainAsync(QuestionFolder, ct)) is [var only])
            {
                return only;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
        }

        throw new TimeoutException("the question never reached the remote");
    }

    private async Task<IReadOnlyList<string>> FilesOnMainAsync(string folder, CancellationToken ct)
    {
        string listing = await _harness.Bus.RunGitAsync(_harness.Bus.BareDir, ["ls-tree", "-r", "--name-only", "main", "--", folder], ct);
        return [.. listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    /// <summary>Pushes a signed decision of Central into <paramref name="folder"/>; returns its path.</summary>
    private async Task<string> SeedDecisionAsync(string folder, DateTimeOffset at, string text, CancellationToken ct)
    {
        var id = EnvelopeId.New(at);
        Model decision = Model.CreateDecision(
            new EnvelopeHeader(CliHarness.Acme, id, CliHarness.Central, CliHarness.HomeLaptop, at, EnvelopePriority.Normal, EnvelopeId.New(at.AddMinutes(-1))),
            new DecisionFields(DecisionKind.Answer, DecisionBasis.Reported, "nothing to undo"),
            System.Text.Encoding.UTF8.GetBytes(text + "\n"));
        return await SeedAsync(folder, decision, ct);
    }

    /// <summary>Pushes a signed question sent by <paramref name="from"/> into <paramref name="folder"/>'s questions folder; returns its path.</summary>
    private async Task<string> SeedQuestionAsync(MachineName folder, MachineName from, DateTimeOffset at, CancellationToken ct)
    {
        Model question = Model.CreateQuestion(
            new EnvelopeHeader(CliHarness.Acme, EnvelopeId.New(at), from, CliHarness.Central, at),
            new QuestionFields("calizr", Default, [], Session),
            System.Text.Encoding.UTF8.GetBytes(Question + "\n"));
        return await SeedAsync(BusPaths.Questions(CliHarness.Acme, folder), question, ct);
    }

    private async Task<string> SeedAsync(string folder, Model envelope, CancellationToken ct)
    {
        EnvelopeResult signed = await new EnvelopeSigner(_harness.Store).SignAsync(envelope, TestKeys.Acme1, ct);
        string path = BusPaths.EnvelopeFile(folder, envelope.Header.Id);
        await _harness.Bus.SeedRawFileAsync(path, EnvelopeWriter.Serialize(signed.Envelope!), "seed: " + envelope.Header.Id.Value, ct);
        return path;
    }
}
