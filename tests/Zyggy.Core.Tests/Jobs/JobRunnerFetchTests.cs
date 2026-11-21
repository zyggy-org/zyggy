using System.Text;

using NSubstitute;

using Zyggy.Core.Envelope;
using Zyggy.Core.Jobs;
using Zyggy.Core.Processes;
using Zyggy.Core.Registry;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Core.Tests.Registry;

namespace Zyggy.Core.Tests.Jobs;

/// <summary>
/// Spec 59 slice 1 (Fake): a <c>fetch</c> job reads one file of a published project at <c>origin/&lt;ref&gt;</c> through git, without the
/// model, and answers with the file attached (AC-1); its refusals (AC-2); a model run's long REPORT travels whole (AC-3); a work machine
/// gets no rule of its own (AC-15).
/// </summary>
public sealed class JobRunnerFetchTests : IDisposable
{
    private static readonly byte[] PlanBytes = Encoding.UTF8.GetBytes("# Plan\r\nline two\n");

    private readonly ScratchProject _project = ScratchProject.Create();

    private readonly JobRunnerHarness _h = new();

    public JobRunnerFetchTests()
    {
        _h.Registry.Replace(RegistryDocumentTests.Central() with
        {
            Projects = [new RegistryProject("shop", _project.Root.Replace('\\', '/'), "main", [], [], null)],
        });
        _h.Processes.Hook = spec => RecordingProcessRunner.SubCommand(spec) == "cat-file" ? CatFile(spec, PlanBytes) : null;
    }

    public void Dispose() => _project.Dispose();

    // AC-1: done without the model; the attachment is the blob's bytes, named after the file.
    [Fact]
    public async Task RunAsync_FetchExistingFile_DoneWithFileAttachedAndModelNeverStarted()
    {
        // Arrange
        var job = await JobRunnerHarness.JobAsync(Fetch("docs/plan.md", "feature/12-cart"));

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        await _h.Model.DidNotReceiveWithAnyArgs().RunAsync(default!, TestContext.Current.CancellationToken);
        new { reply.Status, reply.Attachment!.Name, Bytes = reply.Attachment.Bytes.ToArray() }
            .Should().BeEquivalentTo(new { Status = ReportStatus.Done, Name = "plan.md", Bytes = PlanBytes });
    }

    // AC-1: the blob is read at origin/<ref> after a fetch from origin.
    [Fact]
    public async Task RunAsync_FetchWithRef_FetchesOriginThenReadsOriginRefPath()
    {
        // Arrange
        var job = await JobRunnerHarness.JobAsync(Fetch("docs/plan.md", "feature/12-cart"));

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        _h.Processes.Calls.Select(c => string.Join(' ', c.Arguments)).Should().ContainInOrder(
            "fetch --prune origin", "cat-file -s origin/feature/12-cart:docs/plan.md", "cat-file blob origin/feature/12-cart:docs/plan.md");
    }

    // AC-1: without fetch_ref the project's default branch of the node's own registry applies.
    [Fact]
    public async Task RunAsync_FetchWithoutRef_ReadsDefaultBranch()
    {
        // Arrange
        var job = await JobRunnerHarness.JobAsync(Fetch("docs/plan.md", null));

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        _h.Processes.CallsOf("cat-file").Last().Arguments.Should().Equal("cat-file", "blob", "origin/main:docs/plan.md");
    }

    // AC-1: the body names what was fetched and keeps the text summary.
    [Fact]
    public async Task RunAsync_FetchExistingFile_BodyNamesFileRefSizeAndText()
    {
        // Arrange
        var job = await JobRunnerHarness.JobAsync(Fetch("docs/plan.md", "main"));

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        Encoding.UTF8.GetString(reply.Body.Span).Should().Be("fetched docs/plan.md at origin/main (17 bytes)\n\n# Plan\nline two\n");
    }

    // AC-2: a path with .., an absolute path or a backslash is refused before git runs.
    [Theory]
    [InlineData("../secrets.txt")]
    [InlineData("docs/../../x")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("docs\\plan.md")]
    public async Task RunAsync_FetchPathOutsideRepository_RejectedSchemaUnsupportedFetchPath(string path)
    {
        // Arrange
        var job = await JobRunnerHarness.JobAsync(Fetch(path, "main"));

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        new { reply.Status, reply.Reason, Body = Encoding.UTF8.GetString(reply.Body.Span), CatFile = _h.Processes.CallsOf("cat-file").Count() }
            .Should().BeEquivalentTo(new { Status = ReportStatus.Rejected, Reason = "schema_unsupported", Body = "schema_unsupported: fetch_path\n", CatFile = 0 });
    }

    // AC-2: a file missing at that ref is git_error with git's first error line.
    [Fact]
    public async Task RunAsync_FetchMissingFile_FailedGitErrorWithGitsLine()
    {
        // Arrange
        _h.Processes.Hook = spec => RecordingProcessRunner.SubCommand(spec) == "cat-file"
            ? RecordingProcessRunner.Fail(128, "fatal: path 'docs/gone.md' does not exist in 'origin/main'\nmore\n")
            : null;
        var job = await JobRunnerHarness.JobAsync(Fetch("docs/gone.md", "main"));

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        new { reply.Status, reply.Reason, Body = Encoding.UTF8.GetString(reply.Body.Span) }.Should().BeEquivalentTo(new
        {
            Status = ReportStatus.Failed,
            Reason = "git_error",
            Body = "git_error: fetch: fatal: path 'docs/gone.md' does not exist in 'origin/main'\n",
        });
    }

    // AC-2: a file above jobs.fetchMaxBytes is refused before its bytes are read.
    [Fact]
    public async Task RunAsync_FetchFileAboveCap_RejectedTooLargeBlobNeverRead()
    {
        // Arrange
        _h.NodePolicy = _h.NodePolicy with { FetchMaxBytes = 10 };
        var job = await JobRunnerHarness.JobAsync(Fetch("docs/plan.md", "main"));

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        new { reply.Status, reply.Reason, Body = Encoding.UTF8.GetString(reply.Body.Span), Blob = _h.Processes.CallsOf("cat-file").Count(c => c.Arguments[1] == "blob") }
            .Should().BeEquivalentTo(new { Status = ReportStatus.Rejected, Reason = "schema_unsupported", Body = "schema_unsupported: too large\n", Blob = 0 });
    }

    // AC-15: a project this machine does not publish is unknown_project for a fetch, as for any job — work machine or not, the node has no rule of its own.
    // The work-laptop case is a job addressed to the machine a Central node.json lists in supervision.workMachines; the runner has no
    // work-machine input at all, so the same outcome on both targets is the proof that no rule of its own applies.
    [Theory]
    [InlineData("home-laptop")]
    [InlineData("work-laptop")]
    public async Task RunAsync_FetchUnpublishedProject_RejectedUnknownProjectNoGit(string target)
    {
        // Arrange
        var job = await JobToAsync(target, new JobFields("payroll", Kind: JobKind.Fetch, FetchPath: "a.txt"));

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        new { reply.Status, reply.Reason, Calls = _h.Processes.Calls.Count }
            .Should().BeEquivalentTo(new { Status = ReportStatus.Rejected, Reason = "unknown_project", Calls = 0 });
    }

    // AC-15: on a machine listed in supervision.workMachines a fetch of a published project runs exactly as anywhere else.
    [Fact]
    public async Task RunAsync_FetchOnListedWorkMachine_DoneWithFileAttachedAsOnAnyMachine()
    {
        // Arrange
        var job = await JobToAsync("work-laptop", Fetch("docs/plan.md", "main"));

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        new { reply.Status, reply.Reason, reply.Attachment!.Name, Bytes = reply.Attachment.Bytes.ToArray() }
            .Should().BeEquivalentTo(new { Status = ReportStatus.Done, Reason = (string?)null, Name = "plan.md", Bytes = PlanBytes });
    }

    // AC-3: a REPORT longer than 2,000 characters is kept whole in report.md; the body still holds the cut summary.
    [Fact]
    public async Task RunAsync_ModelReportAbove2000Characters_AttachedWholeBodyCut()
    {
        // Arrange
        var report = "REPORT\n" + new string('x', 2500) + "\n";
        _h.Model.RunAsync(default!, TestContext.Current.CancellationToken).ReturnsForAnyArgs(JobRunnerHarness.Succeeded("preamble\n" + report));
        var job = await JobRunnerHarness.JobAsync(new JobFields("shop"));

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        new { reply.Attachment!.Name, Text = Encoding.UTF8.GetString(reply.Attachment.Bytes.Span), BodyLength = Encoding.UTF8.GetString(reply.Body.Span).Length }
            .Should().BeEquivalentTo(new { Name = "report.md", Text = report, BodyLength = ReportSummary.MaxCharacters + 1 });
    }

    // AC-3: a short REPORT attaches nothing.
    [Fact]
    public async Task RunAsync_ModelReportShort_NoAttachment()
    {
        // Arrange
        var job = await JobRunnerHarness.JobAsync(new JobFields("shop"));

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        reply.Attachment.Should().BeNull();
    }

    private static async Task<Zyggy.Core.Envelope.Envelope> JobToAsync(string target, JobFields fields)
    {
        var store = await TestKeys.StoreWithBothAsync(CancellationToken.None);
        return await TestEnvelopes.SignedJobAsync(
            JobRunnerHarness.Central, Zyggy.Core.Tenancy.MachineName.Parse(target), store, TestKeys.Acme1, "Investigate 502.\n", CancellationToken.None, null, fields);
    }

    private static JobFields Fetch(string path, string? reference) => new("shop", Kind: JobKind.Fetch, FetchPath: path, FetchRef: reference);

    // `cat-file -s` answers the size; `cat-file blob` writes the bytes to the file the runner named, as the real process runner does.
    private static ProcessResult CatFile(ProcessSpec spec, byte[] bytes)
    {
        if (spec.Arguments[1] == "-s")
        {
            return RecordingProcessRunner.Ok(bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
        }

        File.WriteAllBytes(spec.StandardOutputPath!, bytes);
        return RecordingProcessRunner.Ok();
    }
}
