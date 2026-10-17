using System.Globalization;

using Serilog.Core;
using Serilog.Events;

namespace Zyggy.Node;

/// <summary>
/// Adds <c>utc</c> — the event's timestamp in UTC as <c>yyyy-MM-ddTHH:mm:ssZ</c> — so the §11 output template can print a true
/// <c>Z</c> time (Serilog's own <c>{Timestamp}</c> is local time and would mislabel it).
/// </summary>
internal sealed class UtcTimestampEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(propertyFactory);
        string utc = logEvent.Timestamp.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("utc", utc));
    }
}
