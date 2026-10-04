using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Zyggy.Core.Dream;
using Zyggy.Core.Git;
using Zyggy.Core.Models;
using Zyggy.Core.Runs;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Dream;

/// <summary>Step 9: one whole run with a fake model and a recording git, happy path and the batch-loop rules.</summary>
public sealed partial class DreamRunnerTests : IDisposable
{
    private const string Carol = "---\nname: carol\ndescription: Carol\nupdated: 2026-09-28\n---\n- [stated] 2026-09-28: Carol is my sister.\n";
    private const string StatedLine = "- [stated] 2026-09-29: Carol likes green tea.";

    private static readonly string[] ZyggyLines = Enumerable.Range(1, 10)
        .Select(i => $"- [observed] 2026-09-20 [github-inventory 2026-09-20]: zyggy fact {i}.").ToArray();

    private readonly MemoryTree _tree = new(
        ("private/people/_index.md", "---\nname: people\ndescription: People\nupdated: 2026-09-28\n---\n"),
        ("private/people/carol.md", Carol),
        ("business/areas/_index.md", "---\nname: areas\ndescription: Projects\nupdated: 2026-09-28\n---\n"),
        ("business/areas/zyggy.md", "---\nname: zyggy\ndescription: Zyggy\nupdated: 2026-09-28\n---\n" + string.Concat(ZyggyLines.Select(l => l + "\n"))));

    private readonly IModelRunner _model = Substitute.For<IModelRunner>();
    private readonly RecordingProcessRunner _git = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 4, 1, 0, 0, TimeSpan.Zero));
    private readonly Queue<Func<ModelRunRequest, ModelRunResult>> _responses = new();

    public DreamRunnerTests() =>
        _model.RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => _responses.Dequeue()(call.Arg<ModelRunRequest>()));

    public void Dispose() => _tree.Dispose();

    private string State => Path.Combine(_tree.Root, "state");

    private DreamRunner Runner(DreamOptions? options = null)
    {
        var environment = new DreamEnvironment(_tree.Root, MemoryTree.Alice, TimeZoneInfo.Utc, State,
            Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt"), null, "0.0.0-test");
        var prompts = new DreamPrompts();
        return new DreamRunner(environment, options ?? new DreamOptions(), new DreamFiler(_model, prompts, _clock),
            new Compressor(_model, prompts), new GitClient(_git, new GitClientOptions(), _clock), _clock, NullLogger<DreamRunner>.Instance);
    }

    private void ObservedInbox(int count) =>
        _tree.Write("inbox/m365-mail-backfill-2026-09-29.md",
            string.Concat(Enumerable.Range(1, count).Select(i => $"- [observed] 2026-09-29 [m365-mail 2026-09-29]: mail fact {i}.\n")));

    private static IEnumerable<string> Ids(ModelRunRequest request) => LineId().Matches(request.Prompt).Select(m => m.Groups[1].Value);

    /// <summary>A valid proposal that drops every offered (observed) line, optionally with extra edits.</summary>
    private static Func<ModelRunRequest, ModelRunResult> DropAll(Action<TestProposals>? extra = null, decimal cost = 0.25m) => request =>
    {
        var proposal = TestProposals.New();
        foreach (var id in Ids(request))
        {
            proposal.Disposition(id, "dropped", dropReason: "transient");
        }

        extra?.Invoke(proposal);
        return TestModelResults.Succeeded(proposal.Build(), cost);
    };

    private static Func<ModelRunRequest, ModelRunResult> Bad() =>
        DropAll(p => p.Edit("auto/MEMORY.md", append: ["- [stated] 2026-09-29: nope."]));

    private static Func<ModelRunRequest, ModelRunResult> Fail(RunFailureReason reason, string detail) => _ => TestModelResults.Failed(reason, detail);

    private Dictionary<string, string> Disk() =>
        Directory.EnumerateFiles(_tree.PrincipalDirectory, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, f => Convert.ToHexString(File.ReadAllBytes(f)));

    private string[] CommitPaths() => _git.CallsOf("commit").Single().Arguments.SkipWhile(a => a != "--").Skip(1).ToArray();

    private BatchSizeState SavedBatchState() =>
        BatchSizeState.Parse(File.ReadAllText(Path.Combine(State, "dream-batch.json")), new DreamOptions());

    [Fact]
    public async Task Run_EmptyBacklog_NoModelCallNoCommitNothingToDo()
    {
        // Act
        var record = await Runner().RunAsync(DreamTrigger.Manual, TestContext.Current.CancellationToken);

        // Assert
        record.Outcome.Should().Be("nothing_to_do");
        await _model.DidNotReceive().RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>());
        _git.CallsOf("commit").Should().BeEmpty();
        DreamRunRecordStore.ReadLast(State)!.Run.Should().Be(record.Run);
    }

    [Fact]
    public async Task Run_OneAcceptedBatch_WritesFilesLedgerAndCommitsOnce()
    {
        // Arrange
        _tree.Write("inbox/remember-2026-09-29.md", StatedLine + "\n");
        _responses.Enqueue(_ => TestModelResults.Succeeded(TestProposals.New()
            .Disposition("L1", "filed", "private/people/carol.md")
            .Edit("private/people/carol.md", append: [StatedLine]).Build()));

        // Act
        var record = await Runner().RunAsync(DreamTrigger.Nightly, TestContext.Current.CancellationToken);

        // Assert
        record.Outcome.Should().Be("committed");
        record.Pushed.Should().BeTrue();
        record.Commit.Should().Be("0123456789abcdef0123456789abcdef01234567");
        File.ReadAllText(_tree.Full("private/people/carol.md")).Should().Contain(StatedLine).And.Contain("updated: 2026-10-04");
        var ledger = DreamLedger.Load(File.ReadAllText(_tree.Paths.Ledger)).Ledger!;
        ledger.IsConsumed("inbox/remember-2026-09-29.md", LineHash.Of(StatedLine)).Should().BeTrue();
        _git.CallsOf("commit").Should().ContainSingle().Which.StandardInput.Should().StartWith("dream 2026-10-04\n\noutcome: committed");
        _git.CallsOf("add").Single().Arguments.Should().Equal("add", "--", "acme/alice/.dream/ledger.json");
        _git.CallsOf("push").Single().Arguments.Should().Equal("push", "origin", "HEAD:main");
        File.Exists(_tree.Paths.Pending).Should().BeFalse();
        File.ReadAllText(_tree.Full("inbox/remember-2026-09-29.md")).Should().Be(StatedLine + "\n");
    }

    [Fact]
    public async Task Run_LedgerInSameCommitPaths()
    {
        // Arrange
        _tree.Write("inbox/remember-2026-09-29.md", StatedLine + "\n");
        _responses.Enqueue(_ => TestModelResults.Succeeded(TestProposals.New()
            .Disposition("L1", "filed", "private/people/carol.md")
            .Edit("private/people/carol.md", append: [StatedLine]).Build()));

        // Act
        await Runner().RunAsync(DreamTrigger.Manual, TestContext.Current.CancellationToken);

        // Assert
        CommitPaths().Should().Equal("acme/alice/.dream/ledger.json", "acme/alice/private/people/carol.md");
    }

    [Fact]
    public async Task Run_SecondBatchAborts_FirstCommittedOutcomePartial()
    {
        // Arrange
        ObservedInbox(4);
        _responses.Enqueue(DropAll());
        _responses.Enqueue(Bad());

        // Act
        var record = await Runner(new DreamOptions { BatchMaxLines = 2, BatchMinLines = 1 }).RunAsync(DreamTrigger.Manual, TestContext.Current.CancellationToken);

        // Assert
        record.Outcome.Should().Be("partial");
        record.Check.Should().Be("path_refused");
        record.Batches.Select(b => b.Result).Should().Equal("accepted", "aborted:path_refused");
        var ledger = DreamLedger.Load(File.ReadAllText(_tree.Paths.Ledger)).Ledger!;
        ledger.IsConsumed("inbox/m365-mail-backfill-2026-09-29.md", LineHash.Of("- [observed] 2026-09-29 [m365-mail 2026-09-29]: mail fact 1.")).Should().BeTrue();
        ledger.IsConsumed("inbox/m365-mail-backfill-2026-09-29.md", LineHash.Of("- [observed] 2026-09-29 [m365-mail 2026-09-29]: mail fact 3.")).Should().BeFalse();
        _git.CallsOf("commit").Should().ContainSingle();
    }

    [Fact]
    public async Task Run_FirstBatchAborts_NothingWrittenOutcomeAbortedCheckNamed()
    {
        // Arrange
        ObservedInbox(3);
        var before = Disk();
        _responses.Enqueue(Bad());

        // Act
        var record = await Runner().RunAsync(DreamTrigger.Manual, TestContext.Current.CancellationToken);

        // Assert
        record.Outcome.Should().Be("aborted");
        record.Check.Should().Be("path_refused");
        Disk().Should().Equal(before);
        _git.Calls.Select(RecordingProcessRunner.SubCommand).Should().NotContain(["add", "commit", "push"], "only read-only preflight queries ran");
    }

    [Fact]
    public async Task Run_ClaudeErrorAfterAcceptedBatch_PartialWithReason()
    {
        // Arrange
        ObservedInbox(4);
        _responses.Enqueue(DropAll());
        _responses.Enqueue(Fail(RunFailureReason.ClaudeError, "error_max_turns"));

        // Act
        var record = await Runner(new DreamOptions { BatchMaxLines = 2, BatchMinLines = 1 }).RunAsync(DreamTrigger.Manual, TestContext.Current.CancellationToken);

        // Assert
        record.Outcome.Should().Be("partial");
        record.Reason.Should().Be("claude_error");
        record.Detail.Should().Be("error_max_turns");
        _git.CallsOf("commit").Should().ContainSingle();
    }

    [Fact]
    public async Task Run_MaxBatchesReached_StopsCleanlyCommitted()
    {
        // Arrange
        ObservedInbox(4);
        _responses.Enqueue(DropAll());

        // Act
        var record = await Runner(new DreamOptions { BatchMaxLines = 2, BatchMinLines = 1, MaxBatchesPerRun = 1 })
            .RunAsync(DreamTrigger.Manual, TestContext.Current.CancellationToken);

        // Assert
        record.Outcome.Should().Be("committed");
        record.Batches.Should().ContainSingle();
        record.InboxRemaining.Lines.Should().Be(2);
    }

    [Fact]
    public async Task Run_RemainingBudgetBelowCallBudget_NoFurtherCall()
    {
        // Arrange
        ObservedInbox(4);
        _responses.Enqueue(DropAll(cost: 2m));

        // Act
        var record = await Runner(new DreamOptions { BatchMaxLines = 2, BatchMinLines = 1, RunMaxBudgetUsd = 6m, CallMaxBudgetUsd = 5m })
            .RunAsync(DreamTrigger.Manual, TestContext.Current.CancellationToken);

        // Assert
        record.Outcome.Should().Be("committed");
        record.Batches.Should().ContainSingle();
        record.CostUsdTotal.Should().Be(2m);
    }

    [Fact]
    public async Task Run_RunMaxMinutesElapsed_NoFurtherCall()
    {
        // Arrange
        ObservedInbox(4);
        _responses.Enqueue(request =>
        {
            _clock.Advance(TimeSpan.FromMinutes(2));
            return DropAll()(request);
        });

        // Act
        var record = await Runner(new DreamOptions { BatchMaxLines = 2, BatchMinLines = 1, RunMaxMinutes = 1 })
            .RunAsync(DreamTrigger.Manual, TestContext.Current.CancellationToken);

        // Assert
        record.Batches.Should().ContainSingle();
        record.Outcome.Should().Be("committed");
    }

    [Fact]
    public async Task Run_AttributableFailure_HalvesNextBatchSize()
    {
        // Arrange
        ObservedInbox(3);
        _responses.Enqueue(Fail(RunFailureReason.Timeout, "timeout"));

        // Act
        var record = await Runner().RunAsync(DreamTrigger.Manual, TestContext.Current.CancellationToken);

        // Assert
        record.Outcome.Should().Be("failed");
        record.Reason.Should().Be("timeout");
        SavedBatchState().CurrentLines.Should().Be(75);
    }

    [Theory]
    [InlineData("auth")]
    [InlineData("rate_limit")]
    public async Task Run_AuthOrRateLimitFailure_DoesNotHalve(string detail)
    {
        // Arrange
        ObservedInbox(3);
        _responses.Enqueue(Fail(RunFailureReason.ClaudeError, detail));

        // Act
        var record = await Runner().RunAsync(DreamTrigger.Manual, TestContext.Current.CancellationToken);

        // Assert
        record.Outcome.Should().Be("failed");
        record.Detail.Should().Be(detail);
        SavedBatchState().CurrentLines.Should().Be(150);
    }

    [Fact]
    public async Task Run_StuckHeadThreeTimesAtMin_QuarantinesAndContinues()
    {
        // Arrange
        ObservedInbox(10);
        var head = LineHash.Of("- [observed] 2026-09-29 [m365-mail 2026-09-29]: mail fact 1.");
        Directory.CreateDirectory(State);
        File.WriteAllText(Path.Combine(State, "dream-batch.json"), new BatchSizeState(10, 2, head).Serialize());
        _responses.Enqueue(Bad());

        // Act
        var record = await Runner().RunAsync(DreamTrigger.Manual, TestContext.Current.CancellationToken);

        // Assert
        record.Quarantined.Should().Be(10);
        record.Outcome.Should().Be("committed");
        File.ReadAllText(_tree.Paths.Quarantine).Should().Contain(head);
        CommitPaths().Should().Contain("acme/alice/.dream/quarantine.md");
        await _model.Received(1).RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>());

        // And the quarantined lines are never offered again.
        var next = await Runner().RunAsync(DreamTrigger.Manual, TestContext.Current.CancellationToken);
        next.Outcome.Should().Be("nothing_to_do");
    }

    [Fact]
    public async Task Run_RunLevelRemovalLimit_NothingWrittenAborted()
    {
        // Arrange: the batch removes 2 of the 11 durable lines (18 %; the batch limit is 25 %, the run limit 10 %).
        ObservedInbox(2);
        var before = Disk();
        _responses.Enqueue(DropAll(p => p.Edit("business/areas/zyggy.md", remove: [(ZyggyLines[0], "expired"), (ZyggyLines[1], "expired")])));

        // Act
        var record = await Runner().RunAsync(DreamTrigger.Manual, TestContext.Current.CancellationToken);

        // Assert
        record.Outcome.Should().Be("aborted");
        record.Check.Should().Be("run_removal_limit");
        Disk().Should().Equal(before);
        _git.Calls.Select(RecordingProcessRunner.SubCommand).Should().NotContain(["add", "commit", "push"], "only read-only preflight queries ran");
    }

    [Fact]
    public async Task Run_NeverThrows_WhenModelRunnerThrows()
    {
        // Arrange
        ObservedInbox(2);
        _responses.Enqueue(_ => throw new InvalidOperationException("boom"));

        // Act
        var record = await Runner().RunAsync(DreamTrigger.Manual, TestContext.Current.CancellationToken);

        // Assert
        record.Outcome.Should().Be("failed");
        record.Reason.Should().Be("claude_error");
    }

    [Fact]
    public async Task Run_Record_HasNoFactText()
    {
        // Arrange
        _tree.Write("inbox/remember-2026-09-29.md", StatedLine + "\n");
        ObservedInbox(2);
        _responses.Enqueue(request =>
        {
            var p = TestProposals.New().Edit("private/people/carol.md", append: [StatedLine]);
            foreach (var id in Ids(request))
            {
                if (id == "L1")
                {
                    p.Disposition(id, "filed", "private/people/carol.md");
                }
                else
                {
                    p.Disposition(id, "dropped", dropReason: "transient");
                }
            }

            return TestModelResults.Succeeded(p.Build());
        });

        // Act
        var record = await Runner().RunAsync(DreamTrigger.Manual, TestContext.Current.CancellationToken);

        // Assert
        record.Outcome.Should().Be("committed");
        var json = File.ReadAllText(Path.Combine(State, "dream-runs.jsonl"));
        foreach (var fact in new[] { "green tea", "mail fact", "Carol", "zyggy fact" })
        {
            json.Should().NotContain(fact);
        }

        JsonDocument.Parse(json.Trim()).RootElement.GetProperty("outcome").GetString().Should().Be("committed");
    }

    [GeneratedRegex(@"^(L[0-9]+) ", RegexOptions.Multiline)]
    private static partial Regex LineId();
}
