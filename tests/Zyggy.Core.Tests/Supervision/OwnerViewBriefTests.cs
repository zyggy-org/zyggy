using Zyggy.Core.Brief;
using Zyggy.Core.Tests.Brief;

namespace Zyggy.Core.Tests.Supervision;

// AC-28: the brief's "Decided for you" section: one line per decision since the previous brief, no section when there is none.
public sealed class OwnerViewBriefTests
{
    private const string Header = "## Decided for you";

    private static (string Markdown, bool Exceeded) Render(BriefDocument d) => BriefRenderer.RenderMarkdown(d, 100, 10000);

    // AC-28
    [Fact]
    public void Render_TwoDecisions_SectionWithTwoLines()
    {
        // Arrange
        BriefDocument document = BriefScenario.Document() with
        {
            Decided =
            [
                new DecidedLine("09:14", "accept", "home-laptop", "calizr", "01JABC"),
                new DecidedLine("10:02", "next", "work-laptop", null, "01JDEF"),
            ],
        };

        // Act
        string[] lines = Render(document).Markdown.Split('\n');

        // Assert
        int at = Array.IndexOf(lines, Header);
        at.Should().BeGreaterThan(0);
        lines[at + 1].Should().Be("- 09:14 accept — home-laptop, calizr — 01JABC");
        lines[at + 2].Should().Be("- 10:02 next — work-laptop — 01JDEF");
        lines[at + 3].Should().BeEmpty();
    }

    // AC-28
    [Fact]
    public void Render_OneDecision_SectionWithOneLine()
    {
        // Arrange
        BriefDocument document = BriefScenario.Document() with
        {
            Decided = [new DecidedLine("09:14", "accept", "home-laptop", "calizr", "01JABC")],
        };

        // Act
        string[] lines = Render(document).Markdown.Split('\n');

        // Assert
        int at = Array.IndexOf(lines, Header);
        at.Should().BeGreaterThan(0);
        lines[at + 1].Should().Be("- 09:14 accept — home-laptop, calizr — 01JABC");
        lines[at + 2].Should().BeEmpty();
    }

    // AC-28
    [Fact]
    public void Render_NoDecision_NoSection()
    {
        // Act
        string markdown = Render(BriefScenario.Document()).Markdown;

        // Assert
        markdown.Should().NotContain("Decided for you");
        markdown.Should().Be(BriefFixture.GoldenText("brief-10-noideas.md"));
    }
}
