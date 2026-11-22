using System.Globalization;

using Zyggy.Core.Outlook;

using static Zyggy.Core.Tests.Outlook.OutlookTestData;

namespace Zyggy.Core.Tests.Outlook;

// AC-3: the read path gives the same dates under the fr-BE culture as under the invariant culture (the work laptop's
// PowerShell session runs under a French culture). Days above 12 catch a day/month swap.
public sealed class OutlookReaderCultureTests
{
    private static FakeOutlookAccess Mail() =>
        new FakeOutlookAccess()
            .Add(FakeOutlookAccess.DefaultStore, "Inbox", FakeOutlookAccess.Item("m1", Local(2026, 11, 21, 18, 7)))
            .Add(FakeOutlookAccess.DefaultStore, "Inbox", FakeOutlookAccess.Item("m2", Local(2026, 1, 13, 9, 30)));

    // The repository runs with InvariantGlobalization=true (no ICU culture data; CultureInfo.GetCultureInfo("fr-BE") throws),
    // so the test session's culture is a writable clone of the invariant culture carrying fr-BE's date and number formats.
    private static CultureInfo Culture(string name)
    {
        if (name.Length == 0)
        {
            return CultureInfo.InvariantCulture;
        }

        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        var dates = culture.DateTimeFormat;
        dates.DateSeparator = "-";
        dates.ShortDatePattern = "dd-MM-yy";
        dates.LongDatePattern = "dddd d MMMM yyyy";
        dates.ShortTimePattern = "HH:mm";
        dates.LongTimePattern = "HH:mm:ss";
        dates.FullDateTimePattern = "dddd d MMMM yyyy HH:mm:ss";
        dates.AMDesignator = string.Empty;
        dates.PMDesignator = string.Empty;
        dates.FirstDayOfWeek = DayOfWeek.Monday;
        dates.DayNames = ["dimanche", "lundi", "mardi", "mercredi", "jeudi", "vendredi", "samedi"];
        dates.MonthNames = ["janvier", "février", "mars", "avril", "mai", "juin", "juillet", "août", "septembre", "octobre", "novembre", "décembre", string.Empty];
        culture.NumberFormat.NumberDecimalSeparator = ",";
        culture.NumberFormat.NumberGroupSeparator = " ";
        return culture;
    }

    private static string[] ListedDates(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = Culture(culture);
        try
        {
            return [.. Reader(Mail()).List("Inbox", Since(2026, 1, 1)).Value!.Select(m => m.Received)];
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static DateTimeOffset? ParsedSince(string culture, string text)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = Culture(culture);
        try
        {
            return OutlookDates.TryParseSince(text, Zone, out var since) ? since : null;
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void List_UnderFrenchBelgianCulture_WritesTheSameDatesAsInvariant()
    {
        // Act
        var french = ListedDates("fr-BE");

        // Assert
        french.Should().Equal(ListedDates(string.Empty));
    }

    [Fact]
    public void List_UnderFrenchBelgianCulture_WritesIso8601()
    {
        // Act
        var french = ListedDates("fr-BE");

        // Assert
        french.Should().Equal("2026-11-21T18:07:00+01:00", "2026-01-13T09:30:00+01:00");
    }

    [Theory]
    [InlineData("2026-11-13", "2026-11-13T00:00:00+01:00")]
    [InlineData("2026-11-13T08:30:00+01:00", "2026-11-13T08:30:00+01:00")]
    [InlineData("2026-11-13T07:30:00Z", "2026-11-13T07:30:00+00:00")]
    public void TryParseSince_UnderFrenchBelgianCulture_ReadsIso8601(string text, string expected)
    {
        // Act
        var since = ParsedSince("fr-BE", text);

        // Assert
        since.Should().Be(DateTimeOffset.Parse(expected, CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("13/11/2026")]
    [InlineData("13-11-2026")]
    [InlineData("2026-13-11")]
    [InlineData("")]
    public void TryParseSince_LocalCultureFormats_AreRejected(string text)
    {
        // Act
        var since = ParsedSince("fr-BE", text);

        // Assert
        since.Should().BeNull();
    }
}
