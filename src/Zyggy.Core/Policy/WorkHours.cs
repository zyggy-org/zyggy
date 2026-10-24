using System.Globalization;

using TimeZoneConverter;

using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;

namespace Zyggy.Core.Policy;

/// <summary>
/// The machine's working window (founding spec §5 item 3, §6 Concurrency), evaluated in the zone's wall clock so a window written as
/// <c>08:00</c>–<c>19:00</c> stays <c>08:00</c>–<c>19:00</c> across the March and October clock changes. <see langword="null"/> on
/// <see cref="NodePolicy.WorkHours"/> means always inside (A7-2).
/// </summary>
/// <param name="Zone">The zone the window is written in.</param>
/// <param name="Days">The working days.</param>
/// <param name="From">The start of the window, inclusive.</param>
/// <param name="To">The end of the window, exclusive.</param>
public sealed record WorkHours(TimeZoneInfo Zone, IReadOnlySet<DayOfWeek> Days, TimeOnly From, TimeOnly To)
{
    /// <summary>Gets the <c>node.json</c> day names and their <see cref="DayOfWeek"/>.</summary>
    public static IReadOnlyDictionary<string, DayOfWeek> DayNames { get; } = new Dictionary<string, DayOfWeek>(StringComparer.Ordinal)
    {
        ["Mon"] = DayOfWeek.Monday,
        ["Tue"] = DayOfWeek.Tuesday,
        ["Wed"] = DayOfWeek.Wednesday,
        ["Thu"] = DayOfWeek.Thursday,
        ["Fri"] = DayOfWeek.Friday,
        ["Sat"] = DayOfWeek.Saturday,
        ["Sun"] = DayOfWeek.Sunday,
    };

    /// <summary>Whether <paramref name="nowUtc"/> falls inside the window: its day is a working day and <c>From &lt;= time &lt; To</c> in <see cref="Zone"/>.</summary>
    /// <param name="nowUtc">The instant to test.</param>
    /// <returns><see langword="true"/> inside the window.</returns>
    public bool Contains(DateTimeOffset nowUtc)
    {
        DateTimeOffset local = TimeZoneInfo.ConvertTime(nowUtc, Zone);
        var time = TimeOnly.FromTimeSpan(local.TimeOfDay);
        return Days.Contains(local.DayOfWeek) && From <= time && time < To;
    }

    /// <summary>
    /// Resolves the <c>node.json</c> section: the zone through <c>TimeZoneConverter</c> (IANA ids work under invariant globalization on
    /// every OS, spec 07 ex-Q2), the day names through <see cref="DayNames"/>, the bounds as <c>HH:mm</c>. The reader has validated the text.
    /// </summary>
    /// <param name="settings">The section; <see langword="null"/> means no window.</param>
    /// <returns>The window, or <see langword="null"/> for "always inside".</returns>
    /// <exception cref="ArgumentException">Thrown when a value the reader would have refused reaches here (unknown zone, day or clock text).</exception>
    public static WorkHours? FromSettings(WorkHoursSettings? settings)
    {
        if (settings is null)
        {
            return null;
        }

        if (!TZConvert.TryGetTimeZoneInfo(settings.Timezone, out TimeZoneInfo? zone))
        {
            throw new ArgumentException("workHours.timezone: unknown time zone", nameof(settings));
        }

        var days = new HashSet<DayOfWeek>();
        foreach (string name in settings.Days)
        {
            if (!DayNames.TryGetValue(name, out DayOfWeek day))
            {
                throw new ArgumentException("workHours.days: must be Mon..Sun", nameof(settings));
            }

            days.Add(day);
        }

        return new WorkHours(zone, days, Clock(settings.From, "from"), Clock(settings.To, "to"));
    }

    /// <summary>The listener's mode from a window: <see cref="PollMode.Idle"/> outside it, <see cref="PollMode.Active"/> inside or when there is none.</summary>
    /// <param name="hours">The window; <see langword="null"/> means always active.</param>
    /// <returns>The selector.</returns>
    public static PollModeSelector ModeSelector(WorkHours? hours) =>
        now => hours?.Contains(now) == false ? PollMode.Idle : PollMode.Active;

    private static TimeOnly Clock(string text, string key) =>
        TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly time)
            ? time
            : throw new ArgumentException("workHours." + key + ": must be HH:mm", nameof(text));
}
