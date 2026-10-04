using System.Text.Json;

using Zyggy.Core.Dream;

namespace Zyggy.Core.Tests.Dream;

public sealed class DreamRunRecordTests
{
    [Fact]
    public void Serialize_FieldNamesAndWireValues_AsSpec()
    {
        // Arrange
        var record = new DreamRunRecord
        {
            Run = "01JRUN",
            Trigger = DreamTriggerWire.ToWire(DreamTrigger.OnDemand),
            Version = "1.0.0",
            Started = new DateTimeOffset(2026, 10, 4, 1, 0, 0, TimeSpan.Zero),
            Ended = new DateTimeOffset(2026, 10, 4, 1, 5, 0, TimeSpan.Zero),
            Outcome = DreamRunOutcomeWire.ToWire(DreamRunOutcome.NothingToDo),
            Batches = [new DreamBatchRecord(3, 1, 1, 0, new Dictionary<string, int> { ["transient"] = 1 }, 1, 0, 0, 0.1m, 2, 1500, "accepted")],
            Rollup = new DreamRollupRecord(2, 1),
            InboxRemaining = new DreamInboxRemaining(4, 120),
            Withheld = ["auto/MEMORY.md"],
            Commit = "abc",
            Pushed = true,
            CostUsdTotal = 0.1m,
        };

        // Act
        using var json = JsonDocument.Parse(DreamRunRecordStore.Serialize(record));
        var root = json.RootElement;

        // Assert
        root.EnumerateObject().Select(p => p.Name).Should().Contain(
            ["run", "trigger", "version", "started", "ended", "outcome", "batches", "compressions", "quarantined", "rollup", "withheld",
             "inbox_remaining", "commit", "pushed", "cost_usd_total"]);
        root.GetProperty("trigger").GetString().Should().Be("on-demand");
        root.GetProperty("outcome").GetString().Should().Be("nothing_to_do");
        root.GetProperty("batches")[0].GetProperty("files_created").GetInt32().Should().Be(1);
        root.GetProperty("batches")[0].GetProperty("duration_ms").GetInt64().Should().Be(1500);
        root.GetProperty("batches")[0].GetProperty("dropped").GetProperty("transient").GetInt32().Should().Be(1);
        root.GetProperty("rollup").GetProperty("daily_rolled").GetInt32().Should().Be(2);
        root.GetProperty("inbox_remaining").GetProperty("lines").GetInt32().Should().Be(120);
        DreamRunRecordStore.Serialize(record).Should().NotContain("\n");
    }

    [Fact]
    public void Wire_TriggersAndOutcomes_AsSpec()
    {
        // Assert
        Enum.GetValues<DreamTrigger>().Select(DreamTriggerWire.ToWire).Should().Equal("nightly", "on-demand", "manual");
        Enum.GetValues<DreamRunOutcome>().Select(DreamRunOutcomeWire.ToWire).Should().Equal("committed", "nothing_to_do", "aborted", "failed", "partial");
        DreamTriggerWire.TryFromWire("on-demand", out var trigger).Should().BeTrue();
        trigger.Should().Be(DreamTrigger.OnDemand);
        DreamTriggerWire.TryFromWire("daily", out _).Should().BeFalse();
    }

    [Fact]
    public void Store_AppendThenReadLast_ReturnsNewest()
    {
        // Arrange
        var state = Path.Combine(Path.GetTempPath(), "zyggy-ut", Guid.NewGuid().ToString("N"));
        try
        {
            var first = new DreamRunRecord { Run = "A", Trigger = "manual", Version = "1", Started = DateTimeOffset.UnixEpoch, Outcome = "committed" };

            // Act
            DreamRunRecordStore.Append(state, first);
            DreamRunRecordStore.Append(state, first with { Run = "B" });

            // Assert
            DreamRunRecordStore.ReadLast(state)!.Run.Should().Be("B");
            DreamRunRecordStore.ReadLast(Path.Combine(state, "none")).Should().BeNull();
        }
        finally
        {
            Directory.Delete(state, recursive: true);
        }
    }
}
