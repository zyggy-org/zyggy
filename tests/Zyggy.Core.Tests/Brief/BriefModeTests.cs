using Zyggy.Core.Brief;

namespace Zyggy.Core.Tests.Brief;

/// <summary>Spec 35 AC-39 / OD-2: the brief's mode comes from <c>weekend_days</c> in the configured zone, not from UTC.</summary>
public sealed class BriefModeTests
{
    private static readonly TimeZoneInfo Brussels = TimeZoneInfo.CreateCustomTimeZone("Europe/Brussels", TimeSpan.FromHours(2), "Test/Brussels", "Test/Brussels");

    [Theory]
    [InlineData("2026-10-09T21:59:00Z", "Weekday")] // Friday 23:59 in Brussels
    [InlineData("2026-10-09T22:30:00Z", "Weekend")] // Saturday 00:30 in Brussels, still Friday in UTC
    [InlineData("2026-10-10T12:00:00Z", "Weekend")]
    [InlineData("2026-10-11T21:59:00Z", "Weekend")] // Sunday 23:59
    [InlineData("2026-10-11T22:30:00Z", "Weekday")] // Monday 00:30 in Brussels, still Sunday in UTC
    public void Mode_SaturdaySundayInZone_Weekend(string utc, string expected)
    {
        // Act
        var mode = BriefModes.Of(DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture), Brussels, [DayOfWeek.Saturday, DayOfWeek.Sunday]);

        // Assert
        mode.ToString().Should().Be(expected);
    }

    [Fact]
    public void Mode_ConfiguredDays()
    {
        // Arrange
        DayOfWeek[] fridayOnly = [DayOfWeek.Friday];

        // Act / Assert
        BriefModes.Of(new DateTimeOffset(2026, 10, 9, 8, 0, 0, TimeSpan.Zero), Brussels, fridayOnly).Should().Be(BriefMode.Weekend);
        BriefModes.Of(new DateTimeOffset(2026, 10, 10, 8, 0, 0, TimeSpan.Zero), Brussels, fridayOnly).Should().Be(BriefMode.Weekday);
    }
}
