using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Zyggy.Core.Dream;
using Zyggy.Core.Memory;
using Zyggy.Core.Models;
using Zyggy.Core.Tests.Hub;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Dream;

/// <summary>
/// Spec 55 slice 7. AC-15: a <c>Routing:</c> inbox line is filed into <c>business/topics/agent-routing.md</c>, and a proposal that
/// files it anywhere else is refused by the dream's checks; the filing prompt carries the rule under a raised version.
/// AC-16: <c>get_context</c> with the topic <c>agent routing</c> returns the file as the applier wrote it and a routing line still
/// in the inbox.
/// </summary>
public sealed class RoutingKnowledgeTests : IDisposable
{
    internal const string RoutingFile = "business/topics/agent-routing.md";
    private const string Routing = "- [stated] 2026-11-19: Routing: booking bugs go to machine home-laptop, project calizr, agent env-debugger";
    private const string LaterRouting = "- [stated] 2026-11-19: Routing: mail problems go to machine central, project instance, agent mail-doctor";
    private static readonly DateOnly RunDate = new(2026, 11, 20);

    private readonly MemoryTree _tree = new(
        ("business/topics/_index.md", "---\nname: topics\ndescription: Recurring subjects\nupdated: 2026-11-01\n---\n"),
        ("business/topics/booking.md", "---\nname: booking\ndescription: The booking flow of Calizr\nupdated: 2026-11-01\n---\n- [stated] 2026-11-01: Booking runs on Calizr.\n"),
        ("business/areas/_index.md", "---\nname: areas\ndescription: Projects\nupdated: 2026-11-01\n---\n"),
        ("business/areas/calizr.md", "---\nname: calizr\ndescription: Calizr\nupdated: 2026-11-01\n---\n- [stated] 2026-11-01: Calizr is a client project.\n"),
        ("inbox/remember-2026-11-19.md", Routing + "\n"));

    private readonly IModelRunner _model = Substitute.For<IModelRunner>();

    public void Dispose() => _tree.Dispose();

    private static TestProposals IntoRoutingFile() =>
        TestProposals.New()
            .Disposition("L1", "filed", RoutingFile)
            .Create(RoutingFile, "agent routing", "Which kind of problem goes to which machine, project and agent", [Routing], ["routing"]);

    private async Task<(BatchOutcome Outcome, WorkingSet Set)> FileAsync(TestProposals proposal)
    {
        var snapshot = MemorySnapshot.Load(_tree.Paths);
        var batch = BatchPlanner.Plan(snapshot, DreamLedger.Empty(), new HashSet<string>(), 150, 60_000)!;
        batch.Lines.Select(l => l.Text).Should().Equal(Routing);
        _model.RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>()).Returns(TestModelResults.Succeeded(proposal.Build()));
        var set = new WorkingSet(snapshot);
        var context = new DreamRunContext(_tree.Paths, Path.Combine(_tree.Root, "state"), RunDate, new DreamOptions())
        {
            Secrets = SecretPatterns.Load(Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt")).Patterns!,
            CarriedPaths = new HashSet<string>(),
        };
        var filer = new DreamFiler(_model, new DreamPrompts(), new FakeTimeProvider());
        var outcome = await filer.FileBatchAsync(batch, snapshot, set, context, TestContext.Current.CancellationToken);
        return (outcome, set);
    }

    // AC-15: the routing line filed into the routing file is applied and the file holds it.
    [Fact]
    public async Task FileBatch_RoutingLineIntoRoutingFile_AppliedAndFileHoldsLine()
    {
        // Act
        var (outcome, set) = await FileAsync(IntoRoutingFile());

        // Assert
        outcome.Should().BeOfType<BatchAccepted>();
        DreamChecks.FactLines(set.Text(RoutingFile)).Should().Equal(Routing);
    }

    // AC-15: a later routing line merged into the existing routing file is applied as well.
    [Fact]
    public async Task FileBatch_RoutingLineMergedIntoExistingRoutingFile_Applied()
    {
        // Arrange
        _tree.Write(RoutingFile, "---\nname: agent routing\ndescription: Routing\nupdated: 2026-11-18\n---\n" + LaterRouting + "\n");
        var proposal = TestProposals.New().Disposition("L1", "filed", RoutingFile).Edit(RoutingFile, append: [Routing]);

        // Act
        var (outcome, set) = await FileAsync(proposal);

        // Assert
        outcome.Should().BeOfType<BatchAccepted>();
        DreamChecks.FactLines(set.Text(RoutingFile)).Should().Equal(LaterRouting, Routing);
    }

    // AC-15: the checks refuse every other target, whether filed, merged or duplicate, an existing or a new file.
    [Theory]
    [InlineData("filed", "business/areas/calizr.md", false)]
    [InlineData("merged", "business/topics/booking.md", false)]
    [InlineData("duplicate", "business/topics/booking.md", false)]
    [InlineData("filed", "business/topics/routing.md", true)]
    public async Task FileBatch_RoutingLineElsewhere_AbortsFactNotFound(string outcome, string target, bool create)
    {
        // Arrange
        var proposal = TestProposals.New().Disposition("L1", outcome, target);
        proposal = create
            ? proposal.Create(target, "routing", "Routing rules", [Routing])
            : proposal.Edit(target, append: [Routing]);
        var disk = Disk();

        // Act
        var (result, set) = await FileAsync(proposal);

        // Assert
        result.Should().BeOfType<BatchAborted>().Which.Check.Should().Be(DreamCheck.FactNotFound);
        set.ChangedPaths.Should().BeEmpty();
        Disk().Should().Equal(disk);
    }

    // AC-15: the filing prompt carries the rule, and its version went up by one (2 → 3; spec 58 raised it to 4).
    [Fact]
    public void FilingPrompt_StatesRoutingRule_VersionRaised()
    {
        // Act
        var prompt = new DreamPrompts().FilingPrompt;

        // Assert
        prompt.Should().StartWith("prompt-version: 5\n");
        prompt.Should().Contain("`Routing:`").And.Contain("`" + RoutingFile + "`");
    }

    // AC-16: get_context "agent routing" returns the routing file as the applier wrote it, and a routing line still in the inbox.
    [Fact]
    public async Task GetContext_AgentRouting_ReturnsAppliedFileAndInboxRoutingLine()
    {
        // Arrange
        var (outcome, set) = await FileAsync(IntoRoutingFile());
        outcome.Should().BeOfType<BatchAccepted>();
        using var tree = new MemoryTree(
            (RoutingFile, set.Text(RoutingFile)!),
            ("business/areas/calizr.md", "---\nname: calizr\ndescription: Calizr\nupdated: 2026-11-01\n---\n- [stated] 2026-11-01: Calizr is a client project.\n"),
            ("inbox/remember-2026-11-20.md", LaterRouting + "\n"));
        await using var hub = await HubHarness.StartAsync(tree);

        // Act
        var result = await hub.CallAsync("get_context", """{"topic":"agent routing"}""");

        // Assert
        var text = HubHarness.TextOf(result);
        text.Should().Contain("## " + RoutingFile + " — Which kind of problem goes to which machine, project and agent\n" + Routing + "\n");
        text.Should().Contain("## inbox (picked up, not yet filed by the dream)\n" + LaterRouting + "\n");
        text.Should().NotContain("Calizr is a client project.");
    }

    private Dictionary<string, string> Disk() =>
        Directory.EnumerateFiles(_tree.Root, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "state" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .ToDictionary(f => f, f => Convert.ToHexString(File.ReadAllBytes(f)));
}
