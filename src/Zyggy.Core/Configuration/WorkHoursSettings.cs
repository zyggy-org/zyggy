namespace Zyggy.Core.Configuration;

/// <summary>
/// The <c>workHours</c> section of <c>node.json</c> (founding spec §10, §5 item 3, §6): the machine's working window as written —
/// an IANA zone, day names <c>Mon</c>..<c>Sun</c>, and <c>HH:mm</c> bounds. Absent (<see langword="null"/> on
/// <see cref="NodeConfiguration.WorkHours"/>) means always inside work hours (A7-2). <see cref="Policy.WorkHours.FromSettings"/> resolves it.
/// </summary>
/// <param name="Timezone">The IANA time-zone id (<c>Europe/Brussels</c>).</param>
/// <param name="Days">The working days, <c>Mon</c>..<c>Sun</c>, no duplicates.</param>
/// <param name="From">The start of the window, <c>HH:mm</c>, inclusive.</param>
/// <param name="To">The end of the window, <c>HH:mm</c>, exclusive; after <paramref name="From"/>.</param>
public sealed record WorkHoursSettings(string Timezone, IReadOnlyList<string> Days, string From, string To);
