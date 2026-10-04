using Zyggy.Core.Dream;
using Zyggy.Core.Memory;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Dream;

/// <summary>AC-23 (daily roll-up into month files) and AC-24 (closed inbox files deleted after the grace period).</summary>
public sealed class RollupTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 10, 4);
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 10, 0, 0, TimeSpan.Zero);

    private readonly MemoryTree _tree = new();
    private readonly DreamLedger _ledger = DreamLedger.Empty();

    public void Dispose() => _tree.Dispose();

    private static string Day(DateOnly date) => $"daily/{date:yyyy-MM-dd}.md";

    private void Daily(DateOnly date, bool consumed = true)
    {
        var lines = new[] { $"- [observed] 09:15 session a1: work on {date:yyyy-MM-dd}.", $"- [observed] 17:40 session b2: review on {date:yyyy-MM-dd}." };
        _tree.Write(Day(date), string.Concat(lines.Select(l => l + "\n")));
        if (consumed)
        {
            _ledger.Consume(Day(date), lines.Select(LineHash.Of), date.AddDays(1));
        }
    }

    private void Inbox(string name, TimeSpan age, DateOnly? lastConsumed, bool allConsumed = true)
    {
        string[] lines = ["- [stated] 2026-09-01: one.", "- [stated] 2026-09-01: two."];
        _tree.Write("inbox/" + name, string.Concat(lines.Select(l => l + "\n")));
        File.SetLastWriteTimeUtc(_tree.Full("inbox/" + name), (Now - age).UtcDateTime);
        if (lastConsumed is { } date)
        {
            _ledger.Consume("inbox/" + name, allConsumed ? lines.Select(LineHash.Of) : [LineHash.Of(lines[0])], date);
        }
    }

    private RollupPlan Plan() => Rollup.Plan(MemorySnapshot.Load(_tree.Paths), _ledger, Today, Now, new DreamOptions());

    [Fact]
    public void Plan_DailyOlderThan30DaysFullyConsumed_ArchivedUnderDateHeadingInMonthFile()
    {
        // Arrange
        Daily(Today.AddDays(-31));

        // Act
        var plan = Plan();

        // Assert
        var archive = plan.Archives.Should().ContainSingle().Subject;
        archive.MonthPath.Should().Be("daily/2026-09.md");
        archive.Days.Should().Equal("daily/2026-09-03.md");
        var file = MemoryFileReader.Parse(archive.Text);
        file.Name.Should().Be("daily 2026-09");
        file.Description.Should().Be("daily notes of 2026-09 (archive)");
        file.Updated.Should().Be(Today);
        file.BodyLines.Should().Equal("## 2026-09-03", "- [observed] 09:15 session a1: work on 2026-09-03.", "- [observed] 17:40 session b2: review on 2026-09-03.");
    }

    [Fact]
    public void Plan_DailyExactly30DaysOld_Untouched()
    {
        // Arrange
        Daily(Today.AddDays(-30));

        // Assert
        Plan().Archives.Should().BeEmpty();
    }

    [Fact]
    public void Plan_Daily31DaysOld_Rolled()
    {
        // Arrange
        Daily(Today.AddDays(-31));

        // Assert
        Plan().Archives.SelectMany(a => a.Days).Should().Equal("daily/2026-09-03.md");
    }

    [Fact]
    public void Plan_DailiesAcrossMonthEnd_GroupedIntoTwoMonthFilesInDateOrder()
    {
        // Arrange
        Daily(new DateOnly(2026, 9, 1));
        Daily(new DateOnly(2026, 8, 30));
        Daily(new DateOnly(2026, 8, 31));

        // Act
        var plan = Plan();

        // Assert
        plan.Archives.Select(a => a.MonthPath).Should().Equal("daily/2026-08.md", "daily/2026-09.md");
        plan.Archives[0].Days.Should().Equal("daily/2026-08-30.md", "daily/2026-08-31.md");
        MemoryFileReader.Parse(plan.Archives[0].Text).BodyLines.Where(l => l.StartsWith("## ", StringComparison.Ordinal))
            .Should().Equal("## 2026-08-30", "## 2026-08-31");
    }

    [Fact]
    public void Plan_DailyNotFullyConsumed_Untouched()
    {
        // Arrange
        Daily(Today.AddDays(-40), consumed: false);

        // Assert
        Plan().Archives.Should().BeEmpty();
    }

    [Fact]
    public void Plan_TodaysDaily_Untouched()
    {
        // Arrange
        Daily(Today);

        // Assert
        Plan().Archives.Should().BeEmpty();
    }

    [Fact]
    public void Plan_ExistingMonthFile_AppendedNotReplaced()
    {
        // Arrange
        _tree.Write("daily/2026-09.md", "---\nname: daily 2026-09\ndescription: daily notes of 2026-09 (archive)\nupdated: 2026-10-01\n---\n## 2026-09-01\n- [observed] 09:00 session x: earlier.\n");
        Daily(new DateOnly(2026, 9, 2));

        // Act
        var archive = Plan().Archives.Single();

        // Assert
        archive.Text.Should().StartWith("---\nname: daily 2026-09\n");
        MemoryFileReader.Parse(archive.Text).BodyLines.Should().StartWith(["## 2026-09-01", "- [observed] 09:00 session x: earlier.", "## 2026-09-02"]);
    }

    [Fact]
    public void Plan_InboxClosedFullyConsumedPastGrace_Deleted()
    {
        // Arrange
        Inbox("remember-2026-09-20.md", TimeSpan.FromDays(10), Today.AddDays(-8));

        // Assert
        Plan().InboxDeletions.Should().Equal("inbox/remember-2026-09-20.md");
    }

    [Fact]
    public void Plan_InboxDatedTodayMinusOne_NotClosed()
    {
        // Arrange
        Inbox("remember-2026-10-03.md", TimeSpan.FromDays(10), Today.AddDays(-8));

        // Assert
        Plan().InboxDeletions.Should().BeEmpty();
    }

    [Fact]
    public void Plan_InboxModifiedWithin24h_NotClosed()
    {
        // Arrange
        Inbox("remember-2026-09-20.md", TimeSpan.FromHours(23), Today.AddDays(-8));

        // Assert
        Plan().InboxDeletions.Should().BeEmpty();
    }

    [Fact]
    public void Plan_InboxUndatedQuiet24h_Closed()
    {
        // Arrange
        Inbox("context-01J8Y.md", TimeSpan.FromHours(25), Today.AddDays(-8));

        // Assert
        Plan().InboxDeletions.Should().Equal("inbox/context-01J8Y.md");
    }

    [Fact]
    public void Plan_InboxOneLineUnconsumed_Kept()
    {
        // Arrange
        Inbox("remember-2026-09-20.md", TimeSpan.FromDays(10), Today.AddDays(-8), allConsumed: false);

        // Assert
        Plan().InboxDeletions.Should().BeEmpty();
    }

    [Fact]
    public void Plan_InboxLastConsumedWithinGrace_Kept()
    {
        // Arrange
        Inbox("remember-2026-09-20.md", TimeSpan.FromDays(10), Today.AddDays(-6));

        // Assert
        Plan().InboxDeletions.Should().BeEmpty();
    }

    [Fact]
    public void Plan_DeletedFile_LedgerEntryRemoved()
    {
        // Arrange
        Inbox("remember-2026-09-20.md", TimeSpan.FromDays(10), Today.AddDays(-8));
        Daily(Today.AddDays(-31));
        var snapshot = MemorySnapshot.Load(_tree.Paths);
        var set = new WorkingSet(snapshot);

        // Act
        var plan = Rollup.Plan(snapshot, _ledger, Today, Now, new DreamOptions());
        Rollup.Apply(plan, set, _ledger);

        // Assert
        _ledger.Files.Should().BeEmpty();
        set.Exists("inbox/remember-2026-09-20.md").Should().BeFalse();
        set.Exists("daily/2026-09-03.md").Should().BeFalse();
        set.Exists("daily/2026-09.md").Should().BeTrue();
        Rollup.CheckDeletions(set, plan).Should().BeNull();
    }

    [Fact]
    public void CheckDeletions_UnplannedInboxDeletion_UnfiledDeletion()
    {
        // Arrange
        Inbox("remember-2026-09-20.md", TimeSpan.FromDays(1), null);
        var snapshot = MemorySnapshot.Load(_tree.Paths);
        var set = new WorkingSet(snapshot);
        set.Delete("inbox/remember-2026-09-20.md");

        // Act
        var check = Rollup.CheckDeletions(set, new RollupPlan([], []));

        // Assert
        check.Should().Be(DreamCheck.UnfiledDeletion);
    }
}
