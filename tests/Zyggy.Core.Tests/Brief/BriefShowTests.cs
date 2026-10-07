using Zyggy.Core.Brief;

namespace Zyggy.Core.Tests.Brief;

/// <summary>Spec 35 AC-12, AC-56, AC-57, AC-58, AC-67: what <c>show</c> prints and what it records.</summary>
public sealed class BriefShowTests : IDisposable
{
    private readonly BriefFixture _f = new();

    public void Dispose() => _f.Dispose();

    [Fact]
    public void Decide_TodayBrief_TextEqualsGoldenNewLastShownToday()
    {
        // Arrange
        _f.WriteGoldenBrief(BriefFixture.Today);
        _f.WriteWatermark("2026-10-05T04:30:00Z");

        // Act
        var outcome = _f.Show().Decide(null, full: false);

        // Assert
        outcome.Text.Should().Be(BriefFixture.GoldenText("show-weekday.txt"));
        outcome.NewLastShown.Should().Be(BriefFixture.Today);
    }

    [Fact]
    public void Decide_Full_TextEqualsFullGolden()
    {
        // Arrange
        _f.WriteGoldenBrief(BriefFixture.Today);
        _f.WriteWatermark("2026-10-05T04:30:00Z");

        // Act
        var outcome = _f.Show().Decide(null, full: true);

        // Assert
        outcome.Text.Should().Be(BriefFixture.GoldenText("show-weekday-full.txt"));
    }

    [Fact]
    public void Decide_TwoBriefsAfterLastShown_EarlierLineNamesThem()
    {
        // Arrange
        _f.WriteGoldenBrief(BriefFixture.Today);
        _f.WriteGoldenBrief(new DateOnly(2026, 10, 2));
        _f.WriteGoldenBrief(new DateOnly(2026, 10, 3));
        _f.WriteGoldenBrief(new DateOnly(2026, 10, 4));
        _f.WriteLastShown("2026-10-02\n");

        // Act
        var outcome = _f.Show().Decide(null, full: false);

        // Assert
        outcome.Text.Should().EndWith("read-only.\n2 earlier briefs not shown (2026-10-03, 2026-10-04) — say \"show <date>\"\n");
        outcome.NewLastShown.Should().Be(BriefFixture.Today);
    }

    [Fact]
    public void Decide_LastShownAbsent_EveryKeptBriefBeforeTodayListed()
    {
        // Arrange
        _f.WriteGoldenBrief(BriefFixture.Today);
        _f.WriteGoldenBrief(new DateOnly(2026, 10, 4));

        // Act
        var outcome = _f.Show().Decide(null, full: false);

        // Assert
        outcome.Text.Should().EndWith("1 earlier brief not shown (2026-10-04) — say \"show <date>\"\n");
    }

    [Fact]
    public void Decide_LastShownToday_NoEarlierLine()
    {
        // Arrange
        _f.WriteGoldenBrief(BriefFixture.Today);
        _f.WriteGoldenBrief(new DateOnly(2026, 10, 4));
        _f.WriteLastShown("2026-10-06\n");

        // Act
        var outcome = _f.Show().Decide(null, full: false);

        // Assert
        outcome.Text.Should().NotContain("earlier brief");
    }

    [Fact]
    public void Decide_Date_ThatBriefNoEarlierLine_LastShownMaxOfBoth()
    {
        // Arrange
        _f.WriteGoldenBrief(new DateOnly(2026, 10, 3));
        _f.WriteGoldenBrief(new DateOnly(2026, 10, 4));
        _f.WriteLastShown("2026-10-04\n");

        // Act
        var outcome = _f.Show().Decide(new DateOnly(2026, 10, 3), full: false);

        // Assert
        outcome.Text.Should().Contain("<zyggy-brief date=\"2026-10-03\" generated=\"2026-10-03T04:31:00Z\">").And.NotContain("earlier brief");
        outcome.NewLastShown.Should().Be(new DateOnly(2026, 10, 4));
    }

    [Fact]
    public void Decide_DateWithoutBrief_NoBriefForDate()
    {
        // Act
        var outcome = _f.Show().Decide(new DateOnly(2026, 10, 1), full: false);

        // Assert
        outcome.Text.Should().Be("no brief for 2026-10-01\n");
        outcome.NewLastShown.Should().BeNull();
    }

    [Theory]
    [InlineData("2026-10-06T04:45:00Z", "today's brief is not ready yet (expected by 07:00)\n")]
    [InlineData("2026-10-06T05:00:00Z", "no brief run recorded today — runbook 13 \"Brief run failed\"\n")]
    [InlineData("2026-10-06T05:01:00Z", "no brief run recorded today — runbook 13 \"Brief run failed\"\n")]
    public void Decide_NoBriefAt0645_0700_0701_NotReadyThenFailureLine(string utc, string expected)
    {
        // Act
        var outcome = _f.ShowAt(DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture)).Decide(null, full: false);

        // Assert
        outcome.Text.Should().Be(expected);
        outcome.NewLastShown.Should().BeNull();
    }

    [Fact]
    public void Decide_FailureRowInBriefJsonl_LineNamesExitErrorAndRunbook_TextEqualsGolden()
    {
        // Arrange: yesterday's success row, today's failed row, two earlier briefs never shown
        _f.WriteBriefJsonlRow("""{"date":"2026-10-05","ts":"2026-10-05T04:31:00Z","mail":3,"exit":0}""");
        _f.WriteBriefJsonlRow("""{"date":"2026-10-06","ts":"2026-10-06T04:31:00Z","exit":6,"error":"claude run failed (error_during_execution) — runbook 13 \"Model run failed\""}""");
        _f.WriteGoldenBrief(new DateOnly(2026, 10, 3));
        _f.WriteGoldenBrief(new DateOnly(2026, 10, 4));

        // Act
        var outcome = _f.Show().Decide(null, full: false);

        // Assert
        outcome.Text.Should().Be(BriefFixture.GoldenText("show-failure.txt"));
        outcome.NewLastShown.Should().BeNull();
    }

    [Fact]
    public void Decide_FailureRowWithoutRunbook_BriefRunFailedAppended()
    {
        // Arrange
        _f.WriteBriefJsonlRow("""{"date":"2026-10-06","ts":"2026-10-06T04:31:00Z","exit":3,"error":"key: not found in credentials directory or file"}""");

        // Act
        var outcome = _f.Show().Decide(null, full: false);

        // Assert
        outcome.Text.Should().Be("exit 3: key: not found in credentials directory or file — runbook 13 \"Brief run failed\"\n");
    }

    [Fact]
    public void Decide_NotReady_EqualsGolden()
    {
        // Act
        var outcome = _f.ShowAt(new DateTimeOffset(2026, 10, 6, 4, 45, 0, TimeSpan.Zero)).Decide(null, full: false);

        // Assert
        outcome.Text.Should().Be(BriefFixture.GoldenText("show-not-ready.txt"));
    }

    [Fact]
    public void Decide_ExpectByFromSettings_Respected()
    {
        // Arrange: 07:00 local, expect_by 08:00
        var settings = BriefSettings.Defaults with { ExpectBy = new TimeOnly(8, 0) };

        // Act
        var outcome = _f.Show(settings).Decide(null, full: false);

        // Assert
        outcome.Text.Should().Be("today's brief is not ready yet (expected by 08:00)\n");
    }
}
