using Zyggy.Core.Bus.Probe;
using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Tests.Bus.Probe;

/// <summary>Spec 04 AC-26 (stats half): per-target counts and nearest-rank percentiles.</summary>
public sealed class ProbeStatsTests
{
    private static readonly DateTimeOffset T = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);

    private static readonly MachineName Home = MachineName.Parse("home-laptop");

    private static readonly MachineName Work = MachineName.Parse("work-laptop");

    [Fact]
    public void Aggregate_TwoRecordsOneDoneOneTimeout_CountsAndPercentiles()
    {
        // Act
        IReadOnlyList<ProbeStatsRow> rows = ProbeStats.Aggregate([Record(Home, ProbeOutcome.Done, 1234), Record(Home, ProbeOutcome.Timeout, null)]);

        // Assert
        rows.Should().ContainSingle().Which.Should().Be(new ProbeStatsRow(Home, 2, 1, 0, 1, 0, 1234, 1234, 1234, 1234));
    }

    [Fact]
    public void Aggregate_NearestRank_P50AndP95OverTenValues()
    {
        // Arrange: 100, 200, …, 1000
        var records = Enumerable.Range(1, 10).Select(i => Record(Home, ProbeOutcome.Done, i * 100L)).Reverse().ToList();

        // Act
        ProbeStatsRow row = ProbeStats.Aggregate(records).Single();

        // Assert: nearest rank — p50 = value at rank ⌈0.5×10⌉ = 5 → 500; p95 = rank ⌈9.5⌉ = 10 → 1000
        row.P50.Should().Be(500);
        row.P95.Should().Be(1000);
        row.Min.Should().Be(100);
        row.Max.Should().Be(1000);
    }

    [Fact]
    public void Aggregate_FilterTo_OnlyThatMachine()
    {
        // Act
        IReadOnlyList<ProbeStatsRow> rows = ProbeStats.Aggregate(
            [Record(Home, ProbeOutcome.Done, 10), Record(Work, ProbeOutcome.Rejected, null), Record(Work, ProbeOutcome.PushFailed, null)],
            Work);

        // Assert
        rows.Should().ContainSingle().Which.Should().Be(new ProbeStatsRow(Work, 2, 0, 1, 0, 1, null, null, null, null));
    }

    [Fact]
    public void Aggregate_TwoTargets_OrderedByName()
    {
        // Act
        IReadOnlyList<ProbeStatsRow> rows = ProbeStats.Aggregate([Record(Work, ProbeOutcome.Done, 10), Record(Home, ProbeOutcome.Done, 20)]);

        // Assert
        rows.Select(r => r.To).Should().Equal(Home, Work);
    }

    [Fact]
    public void RenderMarkdown_Rows_TableWithSpecColumns()
    {
        // Act
        string table = ProbeStats.RenderMarkdown([new ProbeStatsRow(Home, 2, 1, 0, 1, 0, 1234, 1234, 1234, 1234), new ProbeStatsRow(Work, 1, 0, 0, 0, 1, null, null, null, null)]);

        // Assert
        table.Split('\n').Should().Equal(
            "| to | count | done | rejected | timeout | push_failed | p50 ms | p95 ms | min ms | max ms |",
            "|----|------:|-----:|---------:|--------:|------------:|-------:|-------:|-------:|-------:|",
            "| home-laptop | 2 | 1 | 0 | 1 | 0 | 1234 | 1234 | 1234 | 1234 |",
            "| work-laptop | 1 | 0 | 0 | 0 | 1 | - | - | - | - |",
            string.Empty);
    }

    [Fact]
    public void Aggregate_Empty_NoRows()
    {
        // Act
        IReadOnlyList<ProbeStatsRow> rows = ProbeStats.Aggregate([]);

        // Assert
        rows.Should().BeEmpty();
    }

    private static ProbeRecord Record(MachineName to, ProbeOutcome outcome, long? ms) =>
        new(EnvelopeId.New(T), to, T, ms is null ? null : T.AddMilliseconds(ms.Value), outcome, null, ms);
}
