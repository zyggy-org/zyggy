using System.Text;

using NSubstitute;

using Zyggy.Core.Envelope;
using Zyggy.Core.Models;
using Zyggy.Core.Outlook;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Jobs;

/// <summary>
/// Spec 58 slice 5 (AC-16, AC-9): the job runner screens the report of a job whose read tools left a work mail marker in the node
/// directory before the reply goes to the bus; a job without a marker is answered as before.
/// </summary>
public sealed class JobRunnerWorkMailTests : IDisposable
{
    private readonly ScratchProject _project = ScratchProject.Create();

    private readonly JobRunnerHarness _h = new();

    public void Dispose()
    {
        _project.Dispose();
        if (Directory.Exists(_h.Options.NodeDir))
        {
            Directory.Delete(Path.GetDirectoryName(_h.Options.NodeDir)!, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_JobThatReadWorkMail_ReportWithARegisterNumberWithheld()
    {
        // Arrange
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root));
        OutlookJobRun.Record(OutlookJobRun.MarkerPath(_h.Options.NodeDir!, job.Header.Tenant, job.Header.Id), ["n1"]);
        File.WriteAllText(Path.Combine(_h.Options.NodeDir!, "sensitive-terms.txt"), "patient\n");
        _h.Model.RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>()).Returns(JobRunnerHarness.Succeeded("REPORT\nregister 85.07.30-033.28\n"));

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        (reply.Reason, Encoding.UTF8.GetString(reply.Body.Span).Contains("85.07", StringComparison.Ordinal)).Should().Be(("dlp_filter", false));
    }

    [Fact]
    public async Task RunAsync_JobWithoutAMarker_ReportAsBefore()
    {
        // Arrange
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root));
        _h.Model.RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>()).Returns(JobRunnerHarness.Succeeded("REPORT\nthe patient file\n"));

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        (reply.Reason, Encoding.UTF8.GetString(reply.Body.Span).StartsWith("REPORT\nthe patient file\n", StringComparison.Ordinal), Encoding.UTF8.GetString(reply.Body.Span).Contains("work mail:", StringComparison.Ordinal))
            .Should().Be(((string?)null, true, false));
    }
}
