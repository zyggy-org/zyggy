using Zyggy.Core.Dream;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Dream;

public sealed class DreamCommitterTests
{
    private static DreamRunRecord Record() => new()
    {
        Run = "01JABCDEFGHJKMNPQRSTVWXYZ0",
        Trigger = "nightly",
        Version = "1.0.0",
        Started = new DateTimeOffset(2026, 10, 4, 1, 0, 0, TimeSpan.Zero),
        Outcome = "partial",
        Check = "path_refused",
        Batches =
        [
            new DreamBatchRecord(10, 6, 2, 1, new Dictionary<string, int> { ["transient"] = 1 }, 1, 3, 1, 0.5m, 4, 2000, "accepted"),
            new DreamBatchRecord(10, 0, 0, 0, new Dictionary<string, int>(), 0, 0, 0, 0.2m, 2, 1000, "aborted:path_refused"),
        ],
        Quarantined = 0,
        CostUsdTotal = 0.7m,
    };

    [Fact]
    public void Message_SubjectBodyTrailers_AsSpec()
    {
        // Act
        var message = DreamCommitter.Message(Record(), new DateOnly(2026, 10, 4));

        // Assert
        message.Should().Be(
            "dream 2026-10-04\n\n" +
            "outcome: partial (check path_refused)\n" +
            "batches: 2 (1 accepted), lines: 20, filed: 6, merged: 2, duplicate: 1, dropped: 1\n" +
            "files: 1 created, 3 edited; categories created: 1; compressions: 0; quarantined lines: 0\n" +
            "rollup: 0 daily files rolled, 0 inbox files deleted; withheld: 0\n" +
            "cost: 0.70 USD\n\n" +
            "Zyggy-Run: 01JABCDEFGHJKMNPQRSTVWXYZ0\n" +
            "Zyggy-Trigger: nightly\n");
    }

    [Fact]
    public void Paths_ExactlyRunPathsNeverInboxNeverPending()
    {
        // Act
        var paths = DreamCommitter.RepoPaths(MemoryTree.Alice,
            ["private/people/carol.md", "inbox/remember-2026-09-29.md", ".dream/pending.json", ".dream/ledger.json", "business/clients/_index.md"]);

        // Assert
        paths.Should().Equal("acme/alice/.dream/ledger.json", "acme/alice/business/clients/_index.md", "acme/alice/private/people/carol.md");
    }
}
