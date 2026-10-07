namespace Zyggy.Core.Brief;

/// <summary>Weekday: mail, files, items and ideas; weekend: the header and "For the long run" only (spec 35 OD-2).</summary>
internal enum BriefMode
{
    Weekday,
    Weekend,
}

/// <summary>The mode of a run, from <c>brief.weekend_days</c> and the local date in the configured zone (spec 35 AC-39).</summary>
internal static class BriefModes
{
    public static BriefMode Of(DateTimeOffset now, TimeZoneInfo zone, IReadOnlyList<DayOfWeek> weekendDays)
    {
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(weekendDays);
        return weekendDays.Contains(TimeZoneInfo.ConvertTime(now, zone).DayOfWeek) ? BriefMode.Weekend : BriefMode.Weekday;
    }
}
