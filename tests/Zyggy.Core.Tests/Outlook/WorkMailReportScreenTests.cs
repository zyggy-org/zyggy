using System.Text;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Outlook;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Tests.Outlook;

/// <summary>
/// AC-16 and AC-9 (spec 58 slice 5): the job runner screens the report of a job that read work mail before it is written to the bus.
/// A job whose read tools left a marker on the laptop gets its kept-mail count appended; a report (or its <c>report.md</c>) holding a
/// national register number or a term of the shared list, or a marker without the term list, is withheld as <c>dlp_filter</c> and
/// names nothing of what matched. A job that read no work mail is untouched. The marker is removed after the screen.
/// </summary>
public sealed class WorkMailReportScreenTests : IDisposable
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly EnvelopeId Job = EnvelopeId.Parse("01M81N0000000000000000000A");

    private static readonly DateTimeOffset T = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);

    private readonly string _nodeDir = Path.Combine(Path.GetTempPath(), "zyggy-ut", Guid.NewGuid().ToString("N"));

    public WorkMailReportScreenTests() => Directory.CreateDirectory(_nodeDir);

    private string Marker => OutlookJobRun.MarkerPath(_nodeDir, Acme, Job);

    public void Dispose() => Directory.Delete(_nodeDir, recursive: true);

    [Fact]
    public void Apply_NoMarker_ReplyUnchanged()
    {
        // Arrange
        Terms();
        var reply = Reply("Mentions the patient file.");

        // Act
        var screened = WorkMailReportScreen.Apply(reply, _nodeDir, Acme, Job);

        // Assert
        screened.Should().BeSameAs(reply);
    }

    [Fact]
    public void Apply_CleanReport_DoneWithTheKeptCountAppended()
    {
        // Arrange
        Terms();
        MarkerWith("n1", "t1");

        // Act
        var screened = WorkMailReportScreen.Apply(Reply("REPORT\nUrgent: the Q4 budget.\n"), _nodeDir, Acme, Job);

        // Assert
        (screened.Status, screened.Reason, Text(screened)).Should().Be((ReportStatus.Done, (string?)null, "REPORT\nUrgent: the Q4 budget.\nwork mail: 2 mails kept on this laptop (never-list or in doubt)\n"));
    }

    [Theory]
    [InlineData("Please register 85.07.30-033.28 today.")]
    [InlineData("The Patiënten list moved.")]
    [InlineData("medisch dossier attached")]
    public void Apply_SensitiveReport_WithheldAsDlpFilter_NothingOfItKept(string leaked)
    {
        // Arrange
        Terms();
        MarkerWith("n1");

        // Act
        var screened = WorkMailReportScreen.Apply(Reply("REPORT\n" + leaked + "\n"), _nodeDir, Acme, Job);

        // Assert
        (screened.Status, screened.Reason, Text(screened), screened.Attachment)
            .Should().Be((ReportStatus.Failed, "dlp_filter", "dlp_filter: the work mail report was withheld on the laptop by the sensitive-data filter; 1 mails kept on this laptop (never-list or in doubt)\n", (ReportAttachment?)null));
    }

    [Fact]
    public void Apply_SensitiveTextOnlyInTheWholeReport_Withheld()
    {
        // Arrange
        Terms();
        MarkerWith();
        var reply = Reply("REPORT\nshort\n") with { Attachment = new ReportAttachment("report.md", Encoding.UTF8.GetBytes("REPORT\n... the patient asks ...\n")) };

        // Act
        var screened = WorkMailReportScreen.Apply(reply, _nodeDir, Acme, Job);

        // Assert
        (screened.Reason, screened.Attachment).Should().Be(("dlp_filter", (ReportAttachment?)null));
    }

    [Fact]
    public void Apply_MarkerWithoutTheTermList_Withheld()
    {
        // Arrange
        MarkerWith();

        // Act
        var screened = WorkMailReportScreen.Apply(Reply("REPORT\nUrgent: the Q4 budget.\n"), _nodeDir, Acme, Job);

        // Assert
        screened.Reason.Should().Be("dlp_filter");
    }

    [Fact]
    public void Apply_FailedRunWithSensitivePartialText_Withheld()
    {
        // Arrange
        Terms();
        MarkerWith();
        var reply = new JobReply(ReportStatus.Timeout, "timeout", Encoding.UTF8.GetBytes("timeout after 2 min\n\nthe patient asks\n"), T, T);

        // Act
        var screened = WorkMailReportScreen.Apply(reply, _nodeDir, Acme, Job);

        // Assert
        (screened.Status, screened.Reason).Should().Be((ReportStatus.Failed, "dlp_filter"));
    }

    [Fact]
    public void Apply_RemovesTheMarker()
    {
        // Arrange
        Terms();
        MarkerWith("n1");

        // Act
        WorkMailReportScreen.Apply(Reply("REPORT\nok\n"), _nodeDir, Acme, Job);

        // Assert
        File.Exists(Marker).Should().BeFalse();
    }

    private static JobReply Reply(string body) => new(ReportStatus.Done, null, Encoding.UTF8.GetBytes(body), T, T);

    private static string Text(JobReply reply) => Encoding.UTF8.GetString(reply.Body.Span);

    private void Terms() => File.WriteAllText(Path.Combine(_nodeDir, "sensitive-terms.txt"), "patient\nmedisch\n");

    private void MarkerWith(params string[] kept) => OutlookJobRun.Record(Marker, kept);
}
