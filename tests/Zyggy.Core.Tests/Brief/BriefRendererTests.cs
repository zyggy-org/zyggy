using Zyggy.Core.Brief;

namespace Zyggy.Core.Tests.Brief;

/// <summary>Spec 35 AC-14, AC-20 (text part), AC-63, AC-65, AC-66, AC-67: the rendered brief, golden-tested on the 10-mail scenario.</summary>
public sealed class BriefRendererTests
{
    private static (string Markdown, bool Exceeded) Render(BriefDocument d) => BriefRenderer.RenderMarkdown(d, 40, 3500);

    [Fact]
    public void Render_10_MarkdownByteEqualsGolden()
    {
        // Act
        var (markdown, exceeded) = Render(BriefScenario.Document());

        // Assert
        exceeded.Should().BeFalse();
        markdown.Should().Be(BriefFixture.GoldenText("brief-10-noideas.md"));
    }

    [Fact]
    public void Render_10_NoMarkersPageEqualsFull()
    {
        // Act
        var (markdown, _) = Render(BriefScenario.Document());

        // Assert
        markdown.Should().NotContain("<!--page");
        BriefPayload.Page(markdown).Should().Be(BriefPayload.Full(markdown));
    }

    [Theory]
    [InlineData(2, 40, 5, 10)]
    [InlineData(3, 80, 20, 30)]
    public void Render_40_80_PageWithinCaps_FullHoldsEveryLine(int urgent, int important, int other, int files)
    {
        // Arrange
        var document = BriefScenario.Generated_(urgent, important, other, files);

        // Act
        var (markdown, exceeded) = Render(document);
        var page = BriefPayload.Page(markdown).TrimEnd('\n').Split('\n');
        var full = BriefPayload.Full(markdown).TrimEnd('\n').Split('\n');

        // Assert
        exceeded.Should().BeFalse();
        page.Length.Should().BeLessThanOrEqualTo(40);
        string.Join('\n', page).Length.Should().BeLessThanOrEqualTo(3500);
        full.Count(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^- (! )?[0-9]{2}:[0-9]{2} Sender ")).Should().Be(urgent + important);
        full.Should().NotContain(l => l.Contains("brief full", StringComparison.Ordinal));
        page.Should().Contain(l => l.Contains("brief full", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_HeaderCounts_UrgentImportantOtherFiles()
    {
        // Act
        var (markdown, _) = Render(BriefScenario.Document());

        // Assert
        markdown.Split('\n')[0].Should().Be("Zyggy — morning brief 2026-10-06 (11 new mails since 2026-10-05T04:30:00Z: 1 urgent, 8 important, 2 other; 3 changed files)");
    }

    [Fact]
    public void Render_UrgentFirstWithMark_ThenImportant()
    {
        // Act
        var lines = Render(BriefScenario.Document()).Markdown.Split('\n');

        // Assert
        lines[3].Should().StartWith("- ! 07:12 Carol Example");
        lines[4].Should().StartWith("- 08:05 Bob Example");
        lines.Count(l => l.StartsWith("- ! ", StringComparison.Ordinal)).Should().Be(1);
    }

    [Fact]
    public void Render_OtherMails_OneLineOneItem_AbsentWhenZero()
    {
        // Arrange
        var with = BriefScenario.Document();
        var without = BriefScenario.Generated_(urgent: 1, important: 2, other: 0, files: 0);

        // Act
        var withText = Render(with).Markdown;
        var withoutText = Render(without).Markdown;

        // Assert
        withText.Should().Contain("- 2 other mails, none needing you → Z5\n").And.Contain("Z5. file 2 other mails → Archive\n");
        withoutText.Should().NotContain("other mails");
        without.Items.Should().NotContain(i => i.Kind == ZKind.FileOther);
    }

    [Fact]
    public void Render_Files_TiedOrByOthersKept_RestCounted_NoneWhenEmpty()
    {
        // Arrange
        var none = BriefScenario.Generated_(urgent: 1, important: 1, other: 0, files: 0);

        // Act
        var withFiles = Render(BriefScenario.Document()).Markdown;
        var noFiles = Render(none).Markdown;

        // Assert
        withFiles.Should().Contain("- Q3-report.docx (OneDrive:/Reports, modified 09:38 by Dana Example)").And.Contain("- budget.xlsx (OneDrive:/Finance").And.Contain("- 1 other changed file\n").And.NotContain("notes.md");
        noFiles.Should().Contain("## Work in progress\n- none\n");
    }

    [Fact]
    public void Render_NoEmailAddressAnywhere()
    {
        // Act
        var (markdown, _) = Render(BriefScenario.Document());

        // Assert
        markdown.Should().NotContain("@");
    }

    [Fact]
    public void Render_SubjectFromPrepassNeverFromModel()
    {
        // Arrange: the model's summary claims a different subject; the printed subject is the pre-pass one
        var document = BriefScenario.Document();

        // Act
        var (markdown, _) = Render(document);

        // Assert
        markdown.Should().Contain("Bob Example — Lunch on Thursday? —");
        document.Mail.Select(m => m.Subject).Should().BeEquivalentTo(BriefScenario.Prepass().Mail.Select(m => m.Message.Subject));
    }

    [Fact]
    public void Render_AuditFlagged_FirstLine()
    {
        // Arrange
        var document = BriefScenario.Document() with { Audit = "flagged", AuditReasons = ["1 brief drafts", "reply draft \"RE: x\" has no recorded replied message"] };

        // Act
        var (markdown, _) = Render(document);

        // Assert
        markdown.Split('\n')[0].Should().Be("audit FLAGGED: 1 brief drafts; reply draft \"RE: x\" has no recorded replied message");
        markdown.Split('\n')[1].Should().StartWith("Zyggy — morning brief 2026-10-06");
    }

    [Fact]
    public void Render_Weekend_HeaderAndLongRunOnly()
    {
        // Arrange
        var document = BriefScenario.Document() with { Mode = BriefMode.Weekend, Ideas = [Trip] };

        // Act
        var (markdown, _) = Render(document);

        // Assert
        markdown.Should().Be(BriefFixture.GoldenText("brief-weekend.md"));
    }

    private static readonly IdeaLine Acme = new(1, "acme-renewal-offer", "client", "Prepare the Acme renewal offer before the November talks",
        "The renewal talks start in November", "an outline of the offer", "business/clients/acme-corp.md", "Acme renewal talks start in November");

    private static readonly IdeaLine Trip = new(1, "family-autumn-trip", "family", "Plan the autumn-holiday trip",
        "The school holiday starts on 26 October", null, "private/family/holidays.md", "school autumn holiday from 26 October to 1 November");

    [Fact]
    public void Render_WithIdeas_ByteEqualsGolden()
    {
        // Arrange: spec 35 AC-35 — text, why now, area, prepare or you, the basis file and line
        var document = BriefScenario.Document() with { Ideas = [Acme, Trip with { N = 2 }] };

        // Act
        var (markdown, _) = Render(document);

        // Assert
        markdown.Should().Be(BriefFixture.GoldenText("brief-10-ideas.md"));
    }

    [Fact]
    public void Render_IdeasUnavailable_NoteNamesRunbook()
    {
        // Arrange
        var document = BriefScenario.Document() with { IdeasNote = "claude run failed (error_during_execution)" };

        // Act
        var (markdown, _) = Render(document);

        // Assert
        markdown.Should().EndWith("## For the long run\n- not available today (claude run failed (error_during_execution)) — runbook 13 \"Ideas run failed but mail run succeeded\"\n");
    }

    [Fact]
    public void Render_IdeasOff_SectionOmitted()
    {
        // Arrange: ideas_cap 0
        var document = BriefScenario.Document() with { IdeasOff = true };

        // Act
        var (markdown, _) = Render(document);

        // Assert
        markdown.Should().NotContain("For the long run");
        markdown.Should().EndWith("— the amount is in the attachment\n");
    }
}
