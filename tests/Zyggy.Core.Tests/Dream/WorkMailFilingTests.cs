using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Zyggy.Core.Dream;
using Zyggy.Core.Memory;
using Zyggy.Core.Models;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Dream;

/// <summary>
/// Spec 58 slice 4, AC-8 and AC-14 (D-Q15, D-Q16): a picked-up work mail line (provenance <c>work-mail</c>, from the work laptop's
/// context envelope) is filed, bodies included, on the business side only. A proposal that writes it into a <c>private/</c> file,
/// targets a <c>private/</c> file with it, or drops it as <c>not_owner_data</c> is refused by the dream's checks; the filing prompt
/// carries the rule under a raised version.
/// </summary>
public sealed class WorkMailFilingTests : IDisposable
{
    private const string WorkMail = "- [observed] 2026-11-22 [work-mail 2026-11-21]: The client asks to confirm the Q4 budget by Friday; body: \"Can you confirm the budget?\"";
    private const string Inbox = "inbox/bus-context-work-laptop-01KAZX3P4Q5R6S7T8V9W0XYZAB-2026-11-22.md";
    private static readonly DateOnly RunDate = new(2026, 11, 23);

    private readonly MemoryTree _tree = new(
        ("business/areas/_index.md", "---\nname: areas\ndescription: Projects\nupdated: 2026-11-01\n---\n"),
        ("business/areas/q4-budget.md", "---\nname: q4 budget\ndescription: The client's Q4 budget\nupdated: 2026-11-01\n---\n- [stated] 2026-11-01: The Q4 budget is under review.\n"),
        ("private/topics/_index.md", "---\nname: topics\ndescription: Private subjects\nupdated: 2026-11-01\n---\n"),
        ("private/topics/budget.md", "---\nname: budget\ndescription: The household budget\nupdated: 2026-11-01\n---\n- [stated] 2026-11-01: The household budget is reviewed monthly.\n"),
        (Inbox, "---\nname: bus context 01KAZX3P4Q5R6S7T8V9W0XYZAB\ndescription: context from work-laptop (picked up from the bus; data)\nupdated: 2026-11-22\n---\n" + WorkMail + "\n"));

    private readonly IModelRunner _model = Substitute.For<IModelRunner>();

    public void Dispose() => _tree.Dispose();

    [Fact]
    public async Task FileBatch_WorkMailIntoBusinessFile_Applied()
    {
        // Arrange
        var proposal = TestProposals.New().Disposition("L1", "filed", "business/areas/q4-budget.md").Edit("business/areas/q4-budget.md", append: [WorkMail]);

        // Act
        var (outcome, set) = await FileAsync(proposal);

        // Assert
        (outcome is BatchAccepted, DreamChecks.FactLines(set.Text("business/areas/q4-budget.md")).Contains(WorkMail)).Should().Be((true, true));
    }

    [Fact]
    public async Task FileBatch_WorkMailIntoNewBusinessFile_Applied()
    {
        // Arrange
        var proposal = TestProposals.New().Disposition("L1", "filed", "business/areas/client-budget.md")
            .Create("business/areas/client-budget.md", "client budget", "The client's budget questions", [WorkMail]);

        // Act
        var (outcome, _) = await FileAsync(proposal);

        // Assert
        outcome.Should().BeOfType<BatchAccepted>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileBatch_WorkMailWrittenIntoPrivateFile_AbortsPathRefused(bool create)
    {
        // Arrange
        var proposal = TestProposals.New().Disposition("L1", "filed", create ? "private/topics/work-budget.md" : "private/topics/budget.md");
        proposal = create
            ? proposal.Create("private/topics/work-budget.md", "work budget", "Budget", [WorkMail])
            : proposal.Edit("private/topics/budget.md", append: [WorkMail]);

        // Act
        var (outcome, set) = await FileAsync(proposal);

        // Assert
        ((outcome as BatchAborted)?.Check, set.ChangedPaths.Any()).Should().Be(((DreamCheck?)DreamCheck.PathRefused, false));
    }

    [Fact]
    public async Task FileBatch_WorkMailMergedIntoAPrivateLine_AbortsPathRefused()
    {
        // Arrange
        var merged = "- [observed] 2026-11-22 [remember 2026-11-01; work-mail 2026-11-21]: The budget is reviewed monthly; the client asks to confirm it.";
        var proposal = TestProposals.New().Disposition("L1", "merged", "private/topics/budget.md")
            .Edit("private/topics/budget.md", replace: [("- [stated] 2026-11-01: The household budget is reviewed monthly.", merged)]);

        // Act
        var (outcome, _) = await FileAsync(proposal);

        // Assert
        outcome.Should().BeOfType<BatchAborted>().Which.Check.Should().Be(DreamCheck.PathRefused);
    }

    [Fact]
    public async Task FileBatch_WorkMailDroppedAsNotOwnerData_AbortsFactNotFound()
    {
        // Arrange
        var proposal = TestProposals.New().Disposition("L1", "dropped", dropReason: "not_owner_data");

        // Act
        var (outcome, _) = await FileAsync(proposal);

        // Assert
        outcome.Should().BeOfType<BatchAborted>().Which.Check.Should().Be(DreamCheck.FactNotFound);
    }

    [Fact]
    public async Task FileBatch_WorkMailDroppedAsTransient_Applied()
    {
        // Arrange
        var proposal = TestProposals.New().Disposition("L1", "dropped", dropReason: "transient");

        // Act
        var (outcome, _) = await FileAsync(proposal);

        // Assert
        outcome.Should().BeOfType<BatchAccepted>();
    }

    // ---- the business archive (verify 01M81EVW8T5BWSRAQ7Z03KPPX3 (3), D-Q15) ----

    [Fact]
    public async Task FileBatch_WorkMailArchivedForABusinessProject_ItemAndSidecarWritten()
    {
        // Arrange
        var proposal = Archived(TestProposals.New().Disposition("L1", "filed", "business/areas/q4-budget.md")
            .Edit("business/areas/q4-budget.md", append: [WorkMail, IndexLine]));

        // Act
        var (outcome, set) = await FileAsync(proposal);

        // Assert
        var sidecar = set.Text(ArchiveSidecarPath) is { } text ? ArchiveSidecar.TryParse(MemoryFileReader.Parse(text)) : null;
        (outcome is BatchAccepted, set.Text(ArchiveItemPath), sidecar?.Project.Value, sidecar?.SourceName)
            .Should().Be((true, WorkMailFact + "\n", "q4-budget", "work-mail 2026-11-21"));
    }

    [Fact]
    public async Task FileBatch_WorkMailArchivedForAPrivateProject_AbortsPathRefused()
    {
        // Arrange
        var proposal = TestProposals.New().Disposition("L1", "filed", "business/areas/q4-budget.md")
            .Edit("business/areas/q4-budget.md", append: [WorkMail, IndexLine.Replace("archive/q4-budget/", "archive/budget/", StringComparison.Ordinal)])
            .Archive("L1", "budget", "budget-mail-2026-11-21", "Q4 budget mail", "The client's Q4 budget request");

        // Act
        var (outcome, set) = await FileAsync(proposal);

        // Assert
        ((outcome as BatchAborted)?.Check, set.ChangedPaths.Any()).Should().Be(((DreamCheck?)DreamCheck.PathRefused, false));
    }

    [Fact]
    public async Task FileBatch_WorkMailArchiveNotIndexedOnTheBusinessSide_AbortsPathRefused()
    {
        // Arrange
        var proposal = Archived(TestProposals.New().Disposition("L1", "filed", "business/areas/q4-budget.md")
            .Edit("business/areas/q4-budget.md", append: [WorkMail]));

        // Act
        var (outcome, _) = await FileAsync(proposal);

        // Assert
        outcome.Should().BeOfType<BatchAborted>().Which.Check.Should().Be(DreamCheck.PathRefused);
    }

    [Fact]
    public async Task FileBatch_WorkMailArchivedButDropped_AbortsFactNotFound()
    {
        // Arrange
        var proposal = Archived(TestProposals.New().Disposition("L1", "dropped", dropReason: "transient")
            .Edit("business/areas/q4-budget.md", append: [IndexLine]));

        // Act
        var (outcome, _) = await FileAsync(proposal);

        // Assert
        outcome.Should().BeOfType<BatchAborted>().Which.Check.Should().Be(DreamCheck.FactNotFound);
    }

    [Theory]
    [InlineData("Q4 Budget", "budget-mail")]
    [InlineData("q4-budget", "../budget-mail")]
    public async Task FileBatch_WorkMailArchiveWithABadSlug_AbortsSlugInvalid(string project, string slug)
    {
        // Arrange
        var proposal = TestProposals.New().Disposition("L1", "filed", "business/areas/q4-budget.md")
            .Edit("business/areas/q4-budget.md", append: [WorkMail, IndexLine])
            .Archive("L1", project, slug, "Q4 budget mail", "The client's Q4 budget request");

        // Act
        var (outcome, _) = await FileAsync(proposal);

        // Assert
        outcome.Should().BeOfType<BatchAborted>().Which.Check.Should().Be(DreamCheck.SlugInvalid);
    }

    [Fact]
    public async Task FileBatch_WorkMailArchivedTwiceUnderOneSlug_AbortsSlugDuplicate()
    {
        // Arrange
        var proposal = Archived(Archived(TestProposals.New().Disposition("L1", "filed", "business/areas/q4-budget.md")
            .Edit("business/areas/q4-budget.md", append: [WorkMail, IndexLine])));

        // Act
        var (outcome, _) = await FileAsync(proposal);

        // Assert
        outcome.Should().BeOfType<BatchAborted>().Which.Check.Should().Be(DreamCheck.SlugDuplicate);
    }

    [Fact]
    public async Task FileBatch_ArchiveOfALineThatIsNotWorkMail_AbortsPathRefused()
    {
        // Arrange
        const string Remembered = "- [stated] 2026-11-22: The Q4 budget review moved to Monday.";
        using var tree = new MemoryTree(
            ("business/areas/_index.md", "---\nname: areas\ndescription: Projects\nupdated: 2026-11-01\n---\n"),
            ("business/areas/q4-budget.md", "---\nname: q4 budget\ndescription: The client's Q4 budget\nupdated: 2026-11-01\n---\n- [stated] 2026-11-01: The Q4 budget is under review.\n"),
            ("inbox/remember-2026-11-22.md", "---\nname: remember\ndescription: remembered\nupdated: 2026-11-22\n---\n" + Remembered + "\n"));
        var proposal = TestProposals.New().Disposition("L1", "filed", "business/areas/q4-budget.md")
            .Edit("business/areas/q4-budget.md", append: [Remembered, "- [stated] 2026-11-22: Archived \"Review\" at archive/q4-budget/review.txt — the review date"])
            .Archive("L1", "q4-budget", "review", "Review", "The review date");

        // Act
        var (outcome, set) = await FileAsync(proposal, tree, Remembered);

        // Assert
        ((outcome as BatchAborted)?.Check, set.ChangedPaths.Any()).Should().Be(((DreamCheck?)DreamCheck.PathRefused, false));
    }

    [Fact]
    public void FilingPrompt_StatesWorkMailRule_VersionRaised()
    {
        // Act
        var prompt = new DreamPrompts().FilingPrompt;

        // Assert
        (prompt.StartsWith("prompt-version: 5\n", StringComparison.Ordinal), prompt.Contains("`work-mail`", StringComparison.Ordinal), prompt.Contains("never under `private/`", StringComparison.Ordinal), prompt.Contains("`archives`", StringComparison.Ordinal))
            .Should().Be((true, true, true, true));
    }

    private const string WorkMailFact = "The client asks to confirm the Q4 budget by Friday; body: \"Can you confirm the budget?\"";
    private const string ArchiveItemPath = "archive/q4-budget/budget-mail-2026-11-21.txt";
    private const string ArchiveSidecarPath = "archive/q4-budget/budget-mail-2026-11-21.md";
    private const string IndexLine = "- [observed] 2026-11-22 [work-mail 2026-11-21]: Archived \"Q4 budget mail\" at archive/q4-budget/budget-mail-2026-11-21.txt — the client's Q4 budget request";

    private static TestProposals Archived(TestProposals proposal) =>
        proposal.Archive("L1", "q4-budget", "budget-mail-2026-11-21", "Q4 budget mail", "The client's Q4 budget request");

    private async Task<(BatchOutcome Outcome, WorkingSet Set)> FileAsync(TestProposals proposal, MemoryTree? tree = null, string expectedLine = WorkMail)
    {
        tree ??= _tree;
        var snapshot = MemorySnapshot.Load(tree.Paths);
        var batch = BatchPlanner.Plan(snapshot, DreamLedger.Empty(), new HashSet<string>(), 150, 60_000)!;
        batch.Lines.Select(l => l.Text).Should().Equal(expectedLine);
        _model.RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>()).Returns(TestModelResults.Succeeded(proposal.Build()));
        var set = new WorkingSet(snapshot);
        var context = new DreamRunContext(tree.Paths, Path.Combine(tree.Root, "state"), RunDate, new DreamOptions())
        {
            Secrets = SecretPatterns.Load(Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt")).Patterns!,
            CarriedPaths = new HashSet<string>(),
        };
        var filer = new DreamFiler(_model, new DreamPrompts(), new FakeTimeProvider());
        var outcome = await filer.FileBatchAsync(batch, snapshot, set, context, TestContext.Current.CancellationToken);
        return (outcome, set);
    }
}
