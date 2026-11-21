using Zyggy.Core.Outlook;

namespace Zyggy.Core.Tests.Outlook;

/// <summary>Shared builders for the outlook read-path tests.</summary>
internal static class OutlookTestData
{
    /// <summary>A fixed UTC+01:00 zone without daylight saving, the same on every OS.</summary>
    public static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("zyggy-test+01", TimeSpan.FromHours(1), "zyggy-test+01", "zyggy-test+01");

    public static readonly OutlookPolicyRecord ReadSendMove = new(new DateOnly(2026, 11, 21), new HashSet<string>(["read", "send", "move"], StringComparer.Ordinal));

    public static readonly OutlookPolicyDecision Allowed = new(ReadSendMove, OutlookPolicySource.WorkBoundaryFile, Enabled: true);

    public static OutlookSettings Settings(int bodyMaxChars = OutlookSettings.DefaultReadBodyMaxChars, int maxItems = OutlookSettings.DefaultReadMaxItems) =>
        new(true, null, bodyMaxChars, maxItems);

    public static OutlookReader Reader(FakeOutlookAccess access, OutlookPolicyDecision? policy = null, OutlookSettings? settings = null) =>
        new(access, policy ?? Allowed, settings ?? Settings(), Zone);

    /// <summary>A since-date in the test zone.</summary>
    public static DateTimeOffset Since(int year, int month, int day, int hour = 0) => new(year, month, day, hour, 0, 0, TimeSpan.FromHours(1));

    /// <summary>A local COM time.</summary>
    public static DateTime Local(int year, int month, int day, int hour, int minute = 0) => new(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
}
