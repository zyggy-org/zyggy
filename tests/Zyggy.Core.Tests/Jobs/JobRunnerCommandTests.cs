using System.Text;

using NSubstitute;

using Zyggy.Core.Envelope;
using Zyggy.Core.Policy;
using Zyggy.Core.Registry;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Core.Tests.Registry;

namespace Zyggy.Core.Tests.Jobs;

/// <summary>
/// Spec 59 slice 2 (Fake): a <c>command</c> job runs exactly the argv of its <c>node.json</c> <c>commands</c> entry in the project folder,
/// without the model or a shell (AC-4); an unknown name is refused before any process starts (AC-4); a work machine gets no rule of its
/// own (AC-15).
/// </summary>
public sealed class JobRunnerCommandTests : IDisposable
{
    private static readonly string[] TestArgs = ["test", "--no-build"];

    private readonly ScratchProject _project = ScratchProject.Create();

    private readonly JobRunnerHarness _h = new();

    public JobRunnerCommandTests()
    {
        _h.Registry.Replace(RegistryDocumentTests.Central() with
        {
            Projects = [new RegistryProject("shop", _project.Root.Replace('\\', '/'), "main", [], [], null)],
        });
        _h.NodePolicy = _h.NodePolicy with
        {
            Commands = new Dictionary<string, NodeCommand>(StringComparer.Ordinal)
            {
                ["unit-tests"] = new NodeCommand(["dotnet", "test", "--no-build"], 5),
            },
        };
        _h.Processes.Hook = spec => spec.FileName == "dotnet" ? RecordingProcessRunner.Ok("Passed: 12\n") : null;
    }

    public void Dispose() => _project.Dispose();

    // AC-4: exactly the configured argv, in the project folder, no shell, no envelope text; done with the output attached; no model.
    [Fact]
    public async Task RunAsync_AllowListedCommand_RunsConfiguredArgvInProjectWithoutModel()
    {
        // Arrange
        var job = await JobRunnerHarness.JobAsync(Command("unit-tests"));

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        await _h.Model.DidNotReceiveWithAnyArgs().RunAsync(default!, TestContext.Current.CancellationToken);
        var call = _h.Processes.Calls.Where(c => c.FileName != "git").Should().ContainSingle().Subject;
        new
        {
            call.FileName,
            Arguments = call.Arguments.ToArray(),
            Directory = call.WorkingDirectory,
            call.Timeout,
            reply.Status,
            reply.Reason,
            Body = Encoding.UTF8.GetString(reply.Body.Span),
            reply.Attachment!.Name,
            Output = Encoding.UTF8.GetString(reply.Attachment.Bytes.Span),
        }.Should().BeEquivalentTo(new
        {
            FileName = "dotnet",
            Arguments = TestArgs,
            Directory = _project.Root,
            Timeout = (TimeSpan?)TimeSpan.FromMinutes(5),
            Status = ReportStatus.Done,
            Reason = (string?)null,
            Body = "command unit-tests exited 0\n\nPassed: 12\n",
            Name = "output.txt",
            Output = "Passed: 12\n",
        });
    }

    // AC-4: a non-zero exit is failed with reason command_failed; the body has the exit code and the output's tail.
    [Fact]
    public async Task RunAsync_CommandExitsNonZero_FailedCommandFailedWithExitCode()
    {
        // Arrange
        _h.Processes.Hook = spec => spec.FileName == "dotnet" ? RecordingProcessRunner.Fail(1, "error CS0103\n") : null;
        var job = await JobRunnerHarness.JobAsync(Command("unit-tests"));

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        new { reply.Status, reply.Reason, Body = Encoding.UTF8.GetString(reply.Body.Span) }.Should().BeEquivalentTo(new
        {
            Status = ReportStatus.Failed,
            Reason = "command_failed",
            Body = "command_failed: command unit-tests exited 1\n\nerror CS0103\n",
        });
    }

    // AC-4: a command that runs past its timeoutMinutes is timeout, with the output so far in the body and attached.
    [Fact]
    public async Task RunAsync_CommandPastTimeout_TimeoutWithOutputSoFar()
    {
        // Arrange
        _h.Processes.Hook = spec => spec.FileName == "dotnet"
            ? new Zyggy.Core.Processes.ProcessResult(null, "Running 3 of 12\n", "", TimedOut: true, StartFailed: false, StdoutTruncated: false, TimeSpan.FromMinutes(5))
            : null;
        var job = await JobRunnerHarness.JobAsync(Command("unit-tests"));

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        new { reply.Status, reply.Reason, Body = Encoding.UTF8.GetString(reply.Body.Span), Output = Encoding.UTF8.GetString(reply.Attachment!.Bytes.Span) }
            .Should().BeEquivalentTo(new
            {
                Status = ReportStatus.Timeout,
                Reason = "timeout",
                Body = "timeout after 5 min: command unit-tests\n\nRunning 3 of 12\n",
                Output = "Running 3 of 12\n",
            });
    }

    // AC-4: a program that cannot be started (not on PATH) is failed with reason command_failed, never done.
    [Fact]
    public async Task RunAsync_ProgramCannotStart_FailedCommandFailed()
    {
        // Arrange
        _h.Processes.Hook = spec => spec.FileName == "dotnet"
            ? new Zyggy.Core.Processes.ProcessResult(null, "", "", TimedOut: false, StartFailed: true, StdoutTruncated: false, TimeSpan.Zero)
            : null;
        var job = await JobRunnerHarness.JobAsync(Command("unit-tests"));

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        new { reply.Status, reply.Reason, Body = Encoding.UTF8.GetString(reply.Body.Span) }.Should().BeEquivalentTo(new
        {
            Status = ReportStatus.Failed,
            Reason = "command_failed",
            Body = "command_failed: command unit-tests did not start: program dotnet not found or not executable\n",
        });
    }

    // AC-4 / AC-15: a name not in node.json commands is schema_unsupported before any process starts — on any target, work machine included.
    [Theory]
    [InlineData("home-laptop")]
    [InlineData("work-laptop")]
    public async Task RunAsync_UnknownCommand_RejectedSchemaUnsupportedNoProcess(string target)
    {
        // Arrange
        var job = await JobToAsync(target, Command("rm-rf"));

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        new { reply.Status, reply.Reason, Body = Encoding.UTF8.GetString(reply.Body.Span), Calls = _h.Processes.Calls.Count(c => c.FileName != "git") }
            .Should().BeEquivalentTo(new { Status = ReportStatus.Rejected, Reason = "schema_unsupported", Body = "schema_unsupported: unknown command rm-rf\n", Calls = 0 });
    }

    // AC-15: a project the machine does not publish is unknown_project for a command, on a work machine as on any other.
    [Theory]
    [InlineData("home-laptop")]
    [InlineData("work-laptop")]
    public async Task RunAsync_CommandUnpublishedProject_RejectedUnknownProjectNoProcess(string target)
    {
        // Arrange
        var job = await JobToAsync(target, new JobFields("payroll", Kind: JobKind.Command, Command: "unit-tests"));

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        new { reply.Status, reply.Reason, Calls = _h.Processes.Calls.Count }
            .Should().BeEquivalentTo(new { Status = ReportStatus.Rejected, Reason = "unknown_project", Calls = 0 });
    }

    // AC-15: on a work machine the same allow-listed argv runs; the job's body never reaches it.
    [Fact]
    public async Task RunAsync_CommandOnWorkMachine_SameArgvNoBodyText()
    {
        // Arrange
        var job = await JobToAsync("work-laptop", Command("unit-tests"));

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        _h.Processes.Calls.Where(c => c.FileName != "git").Should().ContainSingle().Which.Arguments.Should().Equal("test", "--no-build");
    }

    private static async Task<Zyggy.Core.Envelope.Envelope> JobToAsync(string target, JobFields fields)
    {
        var store = await TestKeys.StoreWithBothAsync(CancellationToken.None);
        return await TestEnvelopes.SignedJobAsync(
            JobRunnerHarness.Central, Zyggy.Core.Tenancy.MachineName.Parse(target), store, TestKeys.Acme1, "Investigate 502.\n", CancellationToken.None, null, fields);
    }

    private static JobFields Command(string name) => new("shop", Kind: JobKind.Command, Command: name);
}
