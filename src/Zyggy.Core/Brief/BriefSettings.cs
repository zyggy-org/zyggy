using System.Globalization;
using System.Text.Json;

namespace Zyggy.Core.Brief;

/// <summary>What <c>zyggy brief show</c> needs from <c>instance/m365.json</c> (spec 35 Configuration), read from its <c>brief</c> block only.</summary>
internal sealed record BriefSettings(TimeOnly ExpectBy, int KeepDays, IReadOnlyList<DayOfWeek> WeekendDays)
{
    public static BriefSettings Defaults { get; } = new(new TimeOnly(7, 0), 14, [DayOfWeek.Saturday, DayOfWeek.Sunday]);

    /// <summary>
    /// Loads the <c>brief</c> block of <c>&lt;ZYGGY_INSTANCE_DIR&gt;/m365.json</c> (or <c>ZYGGY_M365_CONFIG</c>). The defaults apply when the
    /// variable, the file, the block or a key is absent; the identity part of the file is never read or validated, so an identity problem
    /// cannot silence a brief (plan 35 Assumption 1). A malformed value is a configuration error naming the key.
    /// </summary>
    public static (BriefSettings? Settings, string? Error) Load(IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        string? Get(string key) => environment.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value) ? value : null;

        var path = Get("ZYGGY_M365_CONFIG") ?? (Get("ZYGGY_INSTANCE_DIR") is { } instance ? Path.Join(instance, "m365.json") : null);
        if (path is null || !File.Exists(path))
        {
            return (Defaults, null);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllBytes(path));
        }
        catch (JsonException)
        {
            return (null, $"configuration error: {path} is not JSON");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("brief", out var brief))
            {
                return (Defaults, null);
            }

            if (brief.ValueKind != JsonValueKind.Object)
            {
                return (null, "configuration error: brief is not an object");
            }

            var expectBy = Defaults.ExpectBy;
            if (brief.TryGetProperty("expect_by", out var e))
            {
                if (e.ValueKind != JsonValueKind.String || !TimeOnly.TryParseExact(e.GetString(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out expectBy))
                {
                    return (null, "configuration error: brief.expect_by is not a time HH:MM");
                }
            }

            var keep = Defaults.KeepDays;
            if (brief.TryGetProperty("brief_keep_days", out var k))
            {
                if (k.ValueKind != JsonValueKind.Number || !k.TryGetInt32(out keep) || keep < 1 || keep > 365)
                {
                    return (null, "configuration error: brief.brief_keep_days is not an integer in 1..365");
                }
            }

            var weekend = Defaults.WeekendDays;
            if (brief.TryGetProperty("weekend_days", out var w))
            {
                if (w.ValueKind != JsonValueKind.Array)
                {
                    return (null, "configuration error: brief.weekend_days is not an array of day names");
                }

                var days = new List<DayOfWeek>();
                foreach (var item in w.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String || !Enum.TryParse<DayOfWeek>(item.GetString(), ignoreCase: true, out var day))
                    {
                        return (null, "configuration error: brief.weekend_days is not an array of day names");
                    }

                    days.Add(day);
                }

                weekend = days;
            }

            return (new BriefSettings(expectBy, keep, weekend), null);
        }
    }
}
