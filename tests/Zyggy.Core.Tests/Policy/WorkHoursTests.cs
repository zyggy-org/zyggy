using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;
using Zyggy.Core.Policy;

namespace Zyggy.Core.Tests.Policy;

/// <summary>Spec 07 AC-3 and AC-6 (first half): the working window in the zone's wall clock, across DST, under invariant globalization.</summary>
public sealed class WorkHoursTests
{
    private static readonly WorkHoursSettings Brussels = new("Europe/Brussels", ["Mon", "Tue", "Wed", "Thu", "Fri"], "08:00", "19:00");

    [Theory]
    [InlineData("2026-10-19T05:59:59Z", false)] // Monday 07:59:59 CEST
    [InlineData("2026-10-19T06:00:00Z", true)]  // Monday 08:00:00 CEST
    [InlineData("2026-10-19T16:59:59Z", true)]  // Monday 18:59:59 CEST
    [InlineData("2026-10-19T17:00:00Z", false)] // Monday 19:00:00 CEST — `to` is exclusive
    [InlineData("2026-10-24T10:00:00Z", false)] // Saturday
    public void Contains_BrusselsMonToFri0800To1900_Boundaries(string instant, bool expected)
    {
        // Arrange
        WorkHours hours = WorkHours.FromSettings(Brussels)!;

        // Act
        bool inside = hours.Contains(DateTimeOffset.Parse(instant, System.Globalization.CultureInfo.InvariantCulture));

        // Assert
        inside.Should().Be(expected);
    }

    [Theory]
    [InlineData("2026-03-29T06:30:00Z", false)] // the Sunday DST starts: 08:30 CEST, but a Sunday
    [InlineData("2026-03-30T05:59:00Z", false)] // Monday 07:59 CEST
    [InlineData("2026-03-30T06:00:00Z", true)]  // Monday 08:00 CEST
    [InlineData("2026-10-25T07:00:00Z", false)] // the Sunday DST ends
    [InlineData("2026-10-26T06:59:00Z", false)] // Monday 07:59 CET
    [InlineData("2026-10-26T07:00:00Z", true)]  // Monday 08:00 CET
    public void Contains_AcrossDstChange_LocalWallClock(string instant, bool expected)
    {
        // Arrange
        WorkHours hours = WorkHours.FromSettings(Brussels)!;

        // Act
        bool inside = hours.Contains(DateTimeOffset.Parse(instant, System.Globalization.CultureInfo.InvariantCulture));

        // Assert
        inside.Should().Be(expected);
    }

    [Fact]
    public void FromSettings_Null_Null()
    {
        // Act
        WorkHours? hours = WorkHours.FromSettings(null);

        // Assert
        hours.Should().BeNull();
    }

    [Fact]
    public void FromSettings_IanaId_ResolvesUnderInvariantGlobalization()
    {
        // Arrange: the repo builds every project with InvariantGlobalization=true; the fact must hold there (spec 07 ex-Q2)
        AppContext.TryGetSwitch("System.Globalization.Invariant", out bool invariant).Should().BeTrue();
        invariant.Should().BeTrue("the test runs under the repository's globalization setting");

        // Act
        WorkHours hours = WorkHours.FromSettings(Brussels)!;

        // Assert
        hours.Zone.BaseUtcOffset.Should().Be(TimeSpan.FromHours(1));
        hours.Days.Should().BeEquivalentTo([DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday]);
        hours.From.Should().Be(new TimeOnly(8, 0));
        hours.To.Should().Be(new TimeOnly(19, 0));
    }

    [Fact]
    public void ModeSelector_InsideOutsideNull_ActiveIdleActive()
    {
        // Arrange
        var inside = new DateTimeOffset(2026, 10, 19, 10, 0, 0, TimeSpan.Zero);
        var outside = new DateTimeOffset(2026, 10, 19, 20, 0, 0, TimeSpan.Zero);
        PollModeSelector withHours = WorkHours.ModeSelector(WorkHours.FromSettings(Brussels));
        PollModeSelector always = WorkHours.ModeSelector(null);

        // Act
        PollMode[] modes = [withHours(inside), withHours(outside), always(outside)];

        // Assert
        modes.Should().Equal(PollMode.Active, PollMode.Idle, PollMode.Active);
    }
}
