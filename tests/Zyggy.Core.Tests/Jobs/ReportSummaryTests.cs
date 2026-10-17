using Zyggy.Core.Jobs;
using Zyggy.Core.Models;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Jobs;

/// <summary>Plan 06 Step 5 (AC-8): the §6 summary — REPORT section, else result text, else last assistant text, else a marker; 2,000 characters.</summary>
public sealed class ReportSummaryTests
{
    [Theory]
    [InlineData("REPORT")]
    [InlineData("# REPORT")]
    [InlineData("### REPORT  ")]
    public void Extract_ResultWithReportHeading_FromHeadingToEnd(string heading)
    {
        // Arrange
        var result = Result($"I looked around.\r\n{heading}\nsummary: ok\nREPORT\nagain\n");

        // Act
        var summary = ReportSummary.Extract(result);

        // Assert
        summary.Should().Be($"{heading}\nsummary: ok\nREPORT\nagain\n");
    }

    [Fact]
    public void Extract_ReportHeadingInsideCodeFence_StillMatched()
    {
        // Act
        var summary = ReportSummary.Extract(Result("text\n```\nREPORT\nx\n```\n"));

        // Assert
        summary.Should().Be("REPORT\nx\n```\n");
    }

    [Fact]
    public void Extract_NoHeading_WholeResultText()
    {
        // Act
        var summary = ReportSummary.Extract(Result("A REPORT is mentioned but not a heading.\n"));

        // Assert
        summary.Should().Be("A REPORT is mentioned but not a heading.\n");
    }

    [Fact]
    public void Extract_EmptyResultText_LastAssistantText()
    {
        // Act
        var summary = ReportSummary.Extract(Result(string.Empty) with { LastAssistantText = "Looking at the logs…" });

        // Assert
        summary.Should().Be("Looking at the logs…");
    }

    [Fact]
    public void Extract_Nothing_NoOutputMarker()
    {
        // Act
        var summary = ReportSummary.Extract(Result(null));

        // Assert
        summary.Should().Be("(no output)");
    }

    [Fact]
    public void Extract_Over2000_TruncatedWithEllipsis()
    {
        // Act
        var summary = ReportSummary.Extract(Result(new string('x', 5000)));

        // Assert
        summary.Should().HaveLength(2000).And.EndWith("…");
        summary[..1999].Should().Be(new string('x', 1999));
    }

    [Fact]
    public void Extract_CutInsideSurrogatePair_BacksOffOneUnit()
    {
        // Arrange: the emoji's high surrogate is the 1,999th unit, so a 1,999-unit cut would split it
        var text = new string('x', 1998) + "😀" + new string('y', 100);

        // Act
        var summary = ReportSummary.Extract(Result(text));

        // Assert
        summary.Should().Be(new string('x', 1998) + "…");
    }

    private static ModelRunResult Result(string? text) => JobRunnerHarness.Succeeded(text);
}
