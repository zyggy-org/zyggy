using Zyggy.Core.Brief;

namespace Zyggy.Core.Tests.Brief;

/// <summary>Spec 35 AC-63 (the page cap and its order), AC-68 (no urgent line or urgent action is ever dropped).</summary>
public sealed class PageCapTests
{
    private const int MaxLines = 40;
    private const int MaxChars = 3500;

    private static (IReadOnlyList<string> Lines, bool Exceeded) Render(BriefDocument d) => PageCap.Apply(BriefRenderer.Lines(d), MaxLines, MaxChars);

    private static string[] Page(IReadOnlyList<string> lines) => BriefPayload.Page(string.Join('\n', lines)).Split('\n');

    // A Mail line: "- [! ]HH:MM Sender NNN — …" (an "Only you" line also names the sender, but starts with its action).
    private static bool IsMailLine(string l) => System.Text.RegularExpressions.Regex.IsMatch(l, @"^- (! )?[0-9]{2}:[0-9]{2} Sender ");

    private static int MailOrder(string l) => int.Parse(l.Split(" Sender ")[1][..3], System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void Apply_FitsPage_Unchanged()
    {
        // Act
        var (lines, exceeded) = Render(BriefScenario.Document());

        // Assert
        exceeded.Should().BeFalse();
        lines.Should().NotContain(l => l.StartsWith("<!--page", StringComparison.Ordinal));
        lines.Should().Equal(BriefRenderer.Lines(BriefScenario.Document()).Select(l => l.Text));
    }

    [Fact]
    public void Apply_ShortensImportantMailsLeastRecentFirst_ThenFiles_ThenTrailingItems()
    {
        // Arrange: 30 important mails with nothing to do and 2 files — over the page by a few lines, so some mail lines stay
        // (a busy day with many actions drops every important mail line first: the actions carry the page, see Gate C)
        var document = BriefScenario.Generated_(urgent: 2, important: 30, other: 5, files: 2, actions: false);

        // Act
        var (lines, exceeded) = Render(document);
        var page = Page(lines);

        // Assert
        exceeded.Should().BeFalse();
        page.Length.Should().BeLessThanOrEqualTo(MaxLines);
        string.Join('\n', page).Length.Should().BeLessThanOrEqualTo(MaxChars);
        // the kept important lines are the most recent ones: the first important line printed comes after every dropped one
        var dropped = lines.Where(l => l.StartsWith(BriefPayload.DropMarker, StringComparison.Ordinal) && IsMailLine(l[BriefPayload.DropMarker.Length..])).Select(l => l[BriefPayload.DropMarker.Length..]).ToList();
        var keptImportant = page.Where(l => IsMailLine(l) && !l.StartsWith("- ! ", StringComparison.Ordinal)).ToList();
        dropped.Should().NotBeEmpty();
        keptImportant.Should().NotBeEmpty();
        var lastDroppedOrder = dropped.Max(MailOrder);
        var firstKeptOrder = keptImportant.Min(MailOrder);
        firstKeptOrder.Should().BeGreaterThan(lastDroppedOrder);
        page.Should().Contain(l => l.StartsWith("- … and ", StringComparison.Ordinal) && l.EndsWith("more important mails — say \"brief full\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Apply_StopsAsSoonAsBothLimitsHold()
    {
        // Arrange: a brief two lines over the limit
        var document = BriefScenario.Generated_(urgent: 0, important: 20, other: 0, files: 0);
        var all = BriefRenderer.Lines(document);
        var limit = all.Count - 2;

        // Act
        var (lines, exceeded) = PageCap.Apply(all, limit, 100_000);

        // Assert
        exceeded.Should().BeFalse();
        lines.Count(l => l.StartsWith(BriefPayload.DropMarker, StringComparison.Ordinal)).Should().Be(3, "two lines over plus the count line that takes one");
        Page(lines).Length.Should().Be(limit);
    }

    [Fact]
    public void Apply_NeverDropsUrgentLinesUrgentItemsOrLongRun()
    {
        // Arrange
        var document = BriefScenario.Generated_(urgent: 6, important: 60, other: 10, files: 20) with
        {
            Ideas = [new IdeaLine(1, "a", "career", "idea one"), new IdeaLine(2, "b", "home", "idea two")],
        };

        // Act
        var (lines, _) = Render(document);
        var page = Page(lines);

        // Assert
        page.Count(l => l.StartsWith("- ! ", StringComparison.Ordinal)).Should().Be(6);
        page.Should().Contain("1. idea one").And.Contain("2. idea two");
        var urgentIds = document.Mail.Where(m => m.Class == MailClass.Urgent).Select(m => m.Id).ToList();
        foreach (var item in document.Items.Where(i => i.FromUrgent))
        {
            page.Should().Contain(l => l.StartsWith($"Z{item.N}. ", StringComparison.Ordinal));
        }

        foreach (var you in document.You.Where(y => y.FromUrgent))
        {
            page.Should().Contain(l => l.Contains($"\"{you.Subject}\"", StringComparison.Ordinal));
        }

        urgentIds.Should().HaveCount(6);
    }

    [Fact]
    public void Apply_UrgentAloneOverPage_PageExceededTrue_NothingDropped()
    {
        // Arrange: 45 urgent mails, nothing else
        var document = BriefScenario.Generated_(urgent: 45, important: 0, other: 0, files: 0);

        // Act
        var (lines, exceeded) = Render(document);

        // Assert
        exceeded.Should().BeTrue();
        lines.Should().NotContain(l => l.StartsWith("<!--page", StringComparison.Ordinal));
        lines.Count(l => l.StartsWith("- ! ", StringComparison.Ordinal)).Should().Be(45);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(1234)]
    public void Apply_RandomFixtures_NoUrgentLineOrUrgentActionEverDropped(int seed)
    {
        var random = new Random(seed);
        for (var round = 0; round < 50; round++)
        {
            // Arrange
            var document = BriefScenario.Generated_(random.Next(0, 8), random.Next(0, 80), random.Next(0, 30), random.Next(0, 25), seed * 1000 + round);

            // Act
            var (lines, exceeded) = Render(document);
            var page = Page(lines);

            // Assert
            var urgent = document.Mail.Count(m => m.Class == MailClass.Urgent);
            page.Count(l => l.StartsWith("- ! ", StringComparison.Ordinal)).Should().Be(urgent);
            foreach (var item in document.Items.Where(i => i.FromUrgent))
            {
                page.Should().Contain(l => l.StartsWith($"Z{item.N}. ", StringComparison.Ordinal));
            }

            foreach (var you in document.You.Where(y => y.FromUrgent))
            {
                page.Should().Contain(l => l.StartsWith("- ", StringComparison.Ordinal) && l.Contains($"\"{you.Subject}\" — ", StringComparison.Ordinal));
            }

            if (!exceeded)
            {
                page.Length.Should().BeLessThanOrEqualTo(MaxLines);
                string.Join('\n', page).Length.Should().BeLessThanOrEqualTo(MaxChars);
            }
        }
    }

    [Fact]
    public void Apply_DroppedLinesCarryDropPrefix_CountLinesCarryCountPrefix()
    {
        // Arrange
        var document = BriefScenario.Generated_(urgent: 0, important: 40, other: 0, files: 0);

        // Act
        var (lines, _) = Render(document);

        // Assert
        lines.Should().Contain(l => l.StartsWith(BriefPayload.DropMarker + "- ", StringComparison.Ordinal));
        lines.Should().ContainSingle(l => l.StartsWith(BriefPayload.CountMarker + "- … and ", StringComparison.Ordinal));
        var full = BriefPayload.Full(string.Join('\n', lines)).Split('\n');
        full.Count(IsMailLine).Should().Be(40);
        full.Should().NotContain(l => l.Contains("brief full", StringComparison.Ordinal));
    }
}
