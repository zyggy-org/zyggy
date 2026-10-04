using System.Text.Json;

using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Zyggy.Core.Dream;
using Zyggy.Core.Memory;
using Zyggy.Core.Models;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Dream;

/// <summary>
/// AC-15: every proposal that breaks one rule aborts its batch with the named check (Appendix B, in table order), keeps the
/// pre-batch working set unchanged and writes nothing. Batch lines: L1 = a [stated] inbox line, L2 = a daily line, L3 = an
/// [observed] inbox line.
/// </summary>
public sealed class DreamChecksTests : IDisposable
{
    private const string Stated = "- [stated] 2026-09-29: Carol likes green tea.";
    private const string Daily = "- [observed] 09:15 session abc123: Worked on the Zyggy plan.";
    private const string Observed = "- [observed] 2026-09-29 [m365-mail 2026-09-29]: Zyggy demo planned with Acme.";
    private const string FiledStated = Stated;
    private const string FiledDaily = "- [observed] 2026-09-30 [daily 2026-09-30]: Worked on the Zyggy plan.";
    private const string FiledObserved = Observed;
    private static readonly DateOnly RunDate = new(2026, 10, 4);

    private static readonly string[] ProfileLines = Enumerable.Range(1, 10).Select(i => $"- [stated] 2026-09-18: profile fact {i}.").ToArray();
    private static readonly string[] ZyggyLines = Enumerable.Range(1, 10).Select(i => $"- [observed] 2026-09-20 [github-inventory 2026-09-20]: zyggy fact {i}.").ToArray();

    private readonly MemoryTree _tree = new(
        ("profile.md", "---\nname: profile\ndescription: who Alice is\nupdated: 2026-09-18\n---\n" + string.Concat(ProfileLines.Select(l => l + "\n"))),
        ("preferences.md", "---\nname: preferences\ndescription: how Alice likes help\nupdated: 2026-09-18\n---\n- [stated] 2026-09-18: Short answers.\n"),
        ("agents.md", "- [stated] 2026-09-18: central owns memory.\n"),
        ("auto/MEMORY.md", "auto\n"),
        ("private/people/_index.md", "---\nname: people\ndescription: People\nupdated: 2026-09-28\n---\n"),
        ("private/people/carol.md", "---\nname: carol\ndescription: Carol, the sister of Alice\nupdated: 2026-09-28\n---\n- [stated] 2026-09-28: Carol is my sister.\n"),
        ("business/areas/_index.md", "---\nname: areas\ndescription: Projects\nupdated: 2026-09-28\n---\n"),
        ("business/areas/zyggy.md", "---\nname: zyggy\ndescription: Zyggy\nupdated: 2026-09-28\n---\n" + string.Concat(ZyggyLines.Select(l => l + "\n"))),
        ("inbox/remember-2026-09-29.md", Stated + "\n"),
        ("inbox/m365-mail-backfill-2026-09-29.md", Observed + "\n"),
        ("daily/2026-09-30.md", Daily + "\n"));

    private readonly IModelRunner _model = Substitute.For<IModelRunner>();

    public void Dispose() => _tree.Dispose();

    /// <summary>The valid baseline: every line filed with its provenance.</summary>
    private static TestProposals Valid(bool withL1 = true, bool withL2 = true, bool withL3 = true)
    {
        var p = TestProposals.New();
        if (withL1)
        {
            p.Disposition("L1", "filed", "private/people/carol.md");
        }

        if (withL2)
        {
            p.Disposition("L2", "filed", "business/areas/zyggy.md");
        }

        if (withL3)
        {
            p.Disposition("L3", "filed", "business/areas/zyggy.md");
        }

        return p.Edit("private/people/carol.md", append: [FiledStated])
            .Edit("business/areas/zyggy.md", append: [FiledDaily, FiledObserved]);
    }

    private async Task<(BatchOutcome Outcome, WorkingSet Set)> RunAsync(
        TestProposals proposal,
        DreamOptions? options = null,
        int newCategoriesSoFar = 0,
        IReadOnlySet<string>? carried = null,
        Action? afterSnapshot = null)
    {
        var snapshot = MemorySnapshot.Load(_tree.Paths);
        afterSnapshot?.Invoke();
        var batch = BatchPlanner.Plan(snapshot, DreamLedger.Empty(), new HashSet<string>(), 150, 60_000)!;
        batch.Lines.Select(l => l.Text).Should().Equal(Stated, Daily, Observed);
        _model.RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>()).Returns(TestModelResults.Succeeded(proposal.Build()));
        var set = new WorkingSet(snapshot);
        var context = new DreamRunContext(_tree.Paths, Path.Combine(_tree.Root, "state"), RunDate, options ?? new DreamOptions())
        {
            Secrets = SecretPatterns.Load(Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt")).Patterns!,
            CarriedPaths = carried ?? new HashSet<string>(),
            NewCategoriesSoFar = newCategoriesSoFar,
        };
        var filer = new DreamFiler(_model, new DreamPrompts(), new FakeTimeProvider());
        var outcome = await filer.FileBatchAsync(batch, snapshot, set, context, TestContext.Current.CancellationToken);
        return (outcome, set);
    }

    private async Task Aborts(DreamCheck check, TestProposals proposal, DreamOptions? options = null, int newCategoriesSoFar = 0,
        IReadOnlySet<string>? carried = null, Action? afterSnapshot = null)
    {
        var disk = Disk();
        var (outcome, set) = await RunAsync(proposal, options, newCategoriesSoFar, carried, afterSnapshot);
        outcome.Should().BeOfType<BatchAborted>().Which.Check.Should().Be(check);
        set.ChangedPaths.Should().BeEmpty("the pre-batch working set is unchanged");
        if (afterSnapshot is null)
        {
            Disk().Should().Equal(disk, "nothing reaches disk");
        }
    }

    private Dictionary<string, string> Disk() =>
        Directory.EnumerateFiles(_tree.Root, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "state" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .ToDictionary(f => f, f => Convert.ToHexString(File.ReadAllBytes(f)));

    [Fact]
    public async Task CheckBatch_ValidProposal_ReturnsNull()
    {
        // Act
        var (outcome, set) = await RunAsync(Valid());

        // Assert
        outcome.Should().BeOfType<BatchAccepted>();
        set.ChangedPaths.Should().Equal("business/areas/zyggy.md", "private/people/carol.md");
    }

    // path_refused
    [Fact]
    public Task CheckBatch_PathInAuto_AbortsPathRefused() => Aborts(DreamCheck.PathRefused, Valid().Edit("auto/MEMORY.md", append: [FiledStated]));

    [Fact]
    public Task CheckBatch_PathInInbox_AbortsPathRefused() =>
        Aborts(DreamCheck.PathRefused, Valid().Create("inbox/new.md", "new", "new", [FiledStated]));

    [Fact]
    public Task CheckBatch_PathInDaily_AbortsPathRefused() =>
        Aborts(DreamCheck.PathRefused, Valid().Edit("daily/2026-09-30.md", append: [FiledStated]));

    [Fact]
    public Task CheckBatch_PathInDream_AbortsPathRefused() =>
        Aborts(DreamCheck.PathRefused, Valid().Create(".dream/notes.md", "n", "n", [FiledStated]));

    [Fact]
    public Task CheckBatch_NonMarkdown_AbortsPathRefused() =>
        Aborts(DreamCheck.PathRefused, Valid().Create("private/people/carol-notes.txt", "n", "n", [FiledStated]));

    [Fact]
    public Task CheckBatch_Traversal_AbortsPathRefused() =>
        Aborts(DreamCheck.PathRefused, Valid().Create("../bob/private/people/x.md", "n", "n", [FiledStated]));

    [Fact]
    public Task CheckBatch_AgentsMd_AbortsPathRefused() => Aborts(DreamCheck.PathRefused, Valid().Edit("agents.md", append: [FiledStated]));

    // slug_invalid, slug_duplicate
    [Fact]
    public Task CheckBatch_BadSlug_AbortsSlugInvalid() =>
        Aborts(DreamCheck.SlugInvalid, Valid().Create("private/people/Bad_Name.md", "n", "n", [FiledStated]));

    [Fact]
    public Task CheckBatch_SlugExistsOtherCategory_AbortsSlugDuplicate() =>
        Aborts(DreamCheck.SlugDuplicate, Valid().Create("business/areas/carol.md", "carol", "Carol at work", [FiledStated]));

    // category_invalid, category_cap
    [Fact]
    public Task CheckBatch_BadCategoryName_AbortsCategoryInvalid() =>
        Aborts(DreamCheck.CategoryInvalid, Valid().Create("private/People/dave.md", "dave", "Dave", [FiledStated]));

    [Fact]
    public Task CheckBatch_UnknownCategory_AbortsCategoryInvalid() =>
        Aborts(DreamCheck.CategoryInvalid, Valid().Create("private/pets/rex.md", "rex", "Rex the dog", [FiledStated]));

    [Fact]
    public Task CheckBatch_EmptyNewCategory_AbortsCategoryInvalid() =>
        Aborts(DreamCheck.CategoryInvalid, Valid().NewCategory("business", "clients", "Companies Alice works for"));

    [Fact]
    public Task CheckBatch_SideAtCap_AbortsCategoryCap() =>
        Aborts(DreamCheck.CategoryCap,
            Valid().NewCategory("private", "pets", "Pets").Create("private/pets/rex.md", "rex", "Rex", [FiledStated]),
            new DreamOptions { MaxCategoriesPerSide = 1 });

    [Fact]
    public Task CheckBatch_RunNewCategoryCap_AbortsCategoryCap() =>
        Aborts(DreamCheck.CategoryCap,
            Valid().NewCategory("private", "pets", "Pets").Create("private/pets/rex.md", "rex", "Rex", [FiledStated]),
            new DreamOptions { MaxNewCategoriesPerRun = 1 }, newCategoriesSoFar: 1);

    // format_invalid, foreign_tag, provenance_missing, tag_upgrade
    [Fact]
    public Task CheckBatch_LineOver400_AbortsFormatInvalid() =>
        Aborts(DreamCheck.FormatInvalid, Valid().Edit("private/people/carol.md", append: ["- [stated] 2026-09-29: " + new string('x', 380)]));

    [Fact]
    public Task CheckBatch_MalformedLine_AbortsFormatInvalid() =>
        Aborts(DreamCheck.FormatInvalid, Valid().Edit("private/people/carol.md", append: ["- [stated] yesterday: Carol called."]));

    [Fact]
    public Task CheckBatch_LongDescription_AbortsFormatInvalid() =>
        Aborts(DreamCheck.FormatInvalid, Valid().Edit("private/people/carol.md", description: new string('d', 150)));

    [Fact]
    public Task CheckBatch_TagInferred_AbortsForeignTag() =>
        Aborts(DreamCheck.ForeignTag, Valid().Edit("private/people/carol.md", append: ["- [inferred] 2026-09-29: Carol likes tea."]));

    [Fact]
    public Task CheckBatch_ObservedWithoutProvenance_AbortsProvenanceMissing() =>
        Aborts(DreamCheck.ProvenanceMissing, Valid().Edit("business/areas/zyggy.md", append: ["- [observed] 2026-09-29: Zyggy has a demo."]));

    [Fact]
    public Task CheckBatch_ObservedSourceFiledAsStated_AbortsTagUpgrade() =>
        Aborts(DreamCheck.TagUpgrade, Valid().Edit("business/areas/zyggy.md", append: ["- [stated] 2026-09-28: Zyggy demo planned with Acme."]));

    // identity_observed, identity_shrink
    [Fact]
    public Task CheckBatch_ObservedLineInProfile_AbortsIdentityObserved() =>
        Aborts(DreamCheck.IdentityObserved, Valid().Edit("profile.md", append: ["- [observed] 2026-09-29 [m365-mail 2026-09-29]: Alice works late."]));

    [Fact]
    public Task CheckBatch_ProfileShrinks20Percent_AbortsIdentityShrink() =>
        Aborts(DreamCheck.IdentityShrink, Valid().Edit("profile.md", remove: [(ProfileLines[0], "expired"), (ProfileLines[1], "expired")]));

    // secret_pattern, contact_detail
    [Fact]
    public Task CheckBatch_GithubTokenInLine_AbortsSecretPattern() =>
        Aborts(DreamCheck.SecretPattern, Valid().Edit("business/areas/zyggy.md",
            append: ["- [observed] 2026-09-29 [m365-mail 2026-09-29]: token ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 was shared."]));

    [Fact]
    public Task CheckBatch_SecretInDescription_AbortsSecretPattern() =>
        Aborts(DreamCheck.SecretPattern, Valid().Edit("business/areas/zyggy.md", description: "Zyggy, key sk-ant-api03-ABCDEFGHIJKLMNOPQRSTUVWXYZabcdef"));

    [Fact]
    public Task CheckBatch_EmailInLine_AbortsContactDetail() =>
        Aborts(DreamCheck.ContactDetail, Valid().Edit("private/people/carol.md", append: ["- [stated] 2026-09-29: Carol is carol@example.org."]));

    [Fact]
    public Task CheckBatch_PhoneInLine_AbortsContactDetail() =>
        Aborts(DreamCheck.ContactDetail, Valid().Edit("private/people/carol.md", append: ["- [stated] 2026-09-29: Carol's number is +32 470 12 34 56."]));

    // edit_mismatch, removal_limit
    [Fact]
    public Task CheckBatch_RemoveUnknownLine_AbortsEditMismatch() =>
        Aborts(DreamCheck.EditMismatch, Valid().Edit("private/people/carol.md", remove: [("- [stated] 2026-09-28: Carol is not here.", "expired")]));

    [Fact]
    public Task CheckBatch_RemovesFortyOne_AbortsRemovalLimit() =>
        Aborts(DreamCheck.RemovalLimit, Valid().Edit("business/areas/zyggy.md", remove: [(ZyggyLines[0], "expired"), (ZyggyLines[1], "expired")]),
            new DreamOptions { BatchMaxRemovedLines = 1 });

    [Fact]
    public Task CheckBatch_RemovesThirtyPercent_AbortsRemovalLimit() =>
        Aborts(DreamCheck.RemovalLimit, Valid().Edit("business/areas/zyggy.md",
            remove: [(ZyggyLines[0], "expired"), (ZyggyLines[1], "expired"), (ZyggyLines[2], "expired")]));

    // coverage, stated_dropped
    [Fact]
    public Task CheckBatch_LineWithoutDisposition_AbortsCoverage() => Aborts(DreamCheck.Coverage, Valid(withL2: false));

    [Fact]
    public Task CheckBatch_DoubleDisposition_AbortsCoverage() =>
        Aborts(DreamCheck.Coverage, Valid().Disposition("L1", "duplicate", "private/people/carol.md"));

    [Fact]
    public Task CheckBatch_UnknownLineId_AbortsCoverage() => Aborts(DreamCheck.Coverage, Valid().Disposition("L9", "dropped", dropReason: "transient"));

    [Fact]
    public Task CheckBatch_StatedInboxLineDropped_AbortsStatedDropped() =>
        Aborts(DreamCheck.StatedDropped, Valid(withL1: false).Disposition("L1", "dropped", dropReason: "transient"));

    // fact_not_found
    [Fact]
    public Task CheckBatch_FiledTargetLacksProvenance_AbortsFactNotFound() =>
        Aborts(DreamCheck.FactNotFound, Valid(withL3: false).Disposition("L3", "filed", "private/people/carol.md"));

    [Fact]
    public Task CheckBatch_DuplicateWithoutProvenance_AbortsFactNotFound() =>
        Aborts(DreamCheck.FactNotFound, TestProposals.New()
            .Disposition("L1", "duplicate", "private/people/carol.md")
            .Disposition("L2", "filed", "business/areas/zyggy.md")
            .Disposition("L3", "filed", "business/areas/zyggy.md")
            .Edit("business/areas/zyggy.md", append: [FiledDaily, FiledObserved]));

    [Fact]
    public Task CheckBatch_FiledWithoutTarget_AbortsFactNotFound() =>
        Aborts(DreamCheck.FactNotFound, Valid(withL1: false).Disposition("L1", "filed"));

    // concurrent_edit
    [Fact]
    public Task CheckBatch_TargetEditedOnDiskSinceSnapshot_AbortsConcurrentEdit() =>
        Aborts(DreamCheck.ConcurrentEdit, Valid(),
            afterSnapshot: () => File.AppendAllText(_tree.Full("private/people/carol.md"), "- [stated] 2026-10-04: edited by hand.\n"));

    [Fact]
    public Task CheckBatch_CarriedTarget_AbortsConcurrentEdit() =>
        Aborts(DreamCheck.ConcurrentEdit, Valid(), carried: new HashSet<string> { "private/people/carol.md" });

    [Fact]
    public async Task CheckBatch_RemoveNearlyExistingLine_DetailNamesFileEditNearestLineAndDistanceWithoutFactText()
    {
        // Arrange: the old line differs from the file's line by one character.
        var proposal = Valid().Edit("private/people/carol.md", remove: [("- [stated] 2026-09-28: Carol is my sister!", "expired")]);

        // Act
        var (outcome, _) = await RunAsync(proposal);

        // Assert
        var aborted = outcome.Should().BeOfType<BatchAborted>().Subject;
        aborted.Check.Should().Be(DreamCheck.EditMismatch);
        aborted.Detail.Should().Be("remove #1 in private/people/carol.md (6 lines): old has 42 chars, nearest file line 6 at distance 1; exact text in no other file");
        aborted.Detail.Should().NotContain("Carol is my sister");
    }

    [Fact]
    public async Task CheckBatch_EveryRefusal_HasAFactFreeDetail()
    {
        // Arrange: a sample of refusals whose proposals carry fact text.
        TestProposals[] proposals =
        [
            Valid().Edit("private/people/carol.md", append: ["- [stated] 2026-09-29: Carol's number is +32 470 12 34 56."]),
            Valid().Edit("business/areas/zyggy.md", append: ["- [observed] 2026-09-29 [m365-mail 2026-09-29]: token ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 x."]),
            Valid(withL1: false).Disposition("L1", "dropped", dropReason: "transient"),
            Valid().Edit("private/people/carol.md", append: ["- [inferred] 2026-09-29: Carol likes tea."]),
        ];

        foreach (var proposal in proposals)
        {
            // Act
            var (outcome, _) = await RunAsync(proposal);

            // Assert
            var detail = outcome.Should().BeOfType<BatchAborted>().Subject.Detail;
            detail.Should().NotBeNullOrEmpty();
            detail.Should().NotContain("Carol").And.NotContain("ghp_").And.NotContain("470").And.NotContain("tea");
        }
    }

    [Fact]
    public async Task CheckBatch_EarlierBatchKept_WhenLaterBatchAborts()
    {
        // Arrange
        var snapshot = MemorySnapshot.Load(_tree.Paths);
        var batch = BatchPlanner.Plan(snapshot, DreamLedger.Empty(), new HashSet<string>(), 150, 60_000)!;
        var set = new WorkingSet(snapshot);
        var context = new DreamRunContext(_tree.Paths, Path.Combine(_tree.Root, "state"), RunDate, new DreamOptions())
        {
            Secrets = SecretPatterns.Load(Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt")).Patterns!,
        };
        var filer = new DreamFiler(_model, new DreamPrompts(), new FakeTimeProvider());
        _model.RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>()).Returns(
            TestModelResults.Succeeded(Valid().Build()),
            TestModelResults.Succeeded(Valid().Edit("auto/MEMORY.md", append: [FiledStated]).Build()));

        // Act
        var first = await filer.FileBatchAsync(batch, snapshot, set, context, TestContext.Current.CancellationToken);
        var second = await filer.FileBatchAsync(batch, snapshot, set, context, TestContext.Current.CancellationToken);

        // Assert
        first.Should().BeOfType<BatchAccepted>();
        second.Should().BeOfType<BatchAborted>().Which.Check.Should().Be(DreamCheck.PathRefused);
        set.ChangedPaths.Should().Equal("business/areas/zyggy.md", "private/people/carol.md");
        set.Text("private/people/carol.md").Should().Contain(FiledStated);
    }

    [Fact]
    public void EveryBatchCheck_HasAProducingTest()
    {
        // Arrange: compress_rejected, migration_rejected, run_removal_limit, unfiled_deletion and dirty_pending are produced elsewhere.
        var names = typeof(DreamChecksTests).GetMethods().Select(m => m.Name).ToList();
        var batchChecks = Enum.GetValues<DreamCheck>().Except(
            [DreamCheck.CompressRejected, DreamCheck.MigrationRejected, DreamCheck.RunRemovalLimit, DreamCheck.UnfiledDeletion, DreamCheck.DirtyPending]);

        // Assert
        foreach (var check in batchChecks)
        {
            names.Should().Contain(n => n.EndsWith("_Aborts" + check, StringComparison.Ordinal), check.ToString());
        }

        JsonSerializer.Serialize(names.Count).Should().NotBeNull();
    }
}
