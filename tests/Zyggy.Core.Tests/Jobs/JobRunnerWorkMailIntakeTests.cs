using System.Text;

using NSubstitute;

using Zyggy.Core.Envelope;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Jobs;

/// <summary>
/// Spec 58 decision B as corrected by the owner (2026-11-23): enabling Outlook on a work machine (<c>outlook.enabled</c> true) does not
/// affect other work. A code job is accepted exactly as before; only a <b>mail job</b> (its <c>allowed_tools</c> name <c>zyggy outlook</c>)
/// that holds more than the read verbs and the file reads is refused at intake (<c>rejected</c> / <c>work_mail_code_mix</c>). Separation
/// is kept at run time: the hook learns it runs on a work machine (<c>--work-mail</c>), and a job that is not a mail job gets no work
/// mail marker, so the outlook read tools refuse it.
/// </summary>
public sealed class JobRunnerWorkMailIntakeTests : IDisposable
{
    private readonly ScratchProject _project = ScratchProject.Create();

    private readonly JobRunnerHarness _h = new();

    public void Dispose() => _project.Dispose();

    // AC-B1 (owner correction 2026-11-23): an ordinary development job on the work laptop runs, with its own tools.
    [Fact]
    public async Task RunAsync_WorkMachineCodeJob_AcceptedWithItsTools()
    {
        // Arrange
        _h.NodePolicy = _h.NodePolicy with { WorkMail = true };
        string[] tools = ["Read", "Edit", "Write", "Bash(src/*)", "Bash(dotnet *)"];
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root, AllowedTools: tools));

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        _h.Request().AllowedTools.Should().Equal(tools);
    }

    // AC-B1: a code job that names no tools gets the node's default, bare Bash included, and runs.
    [Fact]
    public async Task RunAsync_WorkMachineNodeDefaultWithBareBash_Accepted()
    {
        // Arrange
        _h.NodePolicy = _h.NodePolicy with { WorkMail = true, DefaultAllowedTools = ["Read", "Bash"] };
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root));

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        _h.Request().AllowedTools.Should().Equal("Read", "Bash");
    }

    // AC-B2: a code job gets no work mail marker, so the outlook read tools refuse it (no Outlook access).
    [Fact]
    public async Task RunAsync_WorkMachineCodeJob_NoWorkMailMarker()
    {
        // Arrange
        _h.NodePolicy = _h.NodePolicy with { WorkMail = true };
        _h.Options.ModelEnvironment = new Dictionary<string, string> { ["ZYGGY_JOB_MARKER"] = "elsewhere" };
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root, AllowedTools: ["Read", "Edit", "Bash(dotnet *)"]));

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        _h.Request().Environment.Should().Contain("ZYGGY_JOB_MARKER", string.Empty);
    }

    // AC-B1: a mail job that can also run code is still refused at intake, the model never started, no lock left behind.
    [Fact]
    public async Task RunAsync_WorkMachineMailJobWithCode_RejectedWorkMailCodeMixModelNeverStarted()
    {
        // Arrange
        _h.NodePolicy = _h.NodePolicy with { WorkMail = true };
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root, AllowedTools: ["Bash(zyggy outlook *)", "Bash(python *)"]));

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        new { reply.Status, reply.Reason, Body = Encoding.UTF8.GetString(reply.Body.Span), ModelCalls = _h.Model.ReceivedCalls().Count(), Hook = _h.Processes.CallsOf("hook").Count(), Lock = File.Exists(_project.LockPath) }
            .Should().BeEquivalentTo(new
            {
                Status = ReportStatus.Rejected,
                Reason = "work_mail_code_mix",
                Body = "work_mail_code_mix: allowed_tools can read work mail (Bash(zyggy outlook *)) and run code (Bash(python *)) on a work machine\n",
                ModelCalls = 0,
                Hook = 0,
                Lock = false,
            });
    }

    // AC-B1: a mail job with Edit or a bare Bash is still refused.
    [Theory]
    [InlineData("Edit")]
    [InlineData("Bash")]
    public async Task RunAsync_WorkMachineMailJobWithEditOrBareBash_Rejected(string extra)
    {
        // Arrange
        _h.NodePolicy = _h.NodePolicy with { WorkMail = true };
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root, AllowedTools: ["Read", "Bash(zyggy outlook *)", extra]));

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        (reply.Status, reply.Reason).Should().Be((ReportStatus.Rejected, "work_mail_code_mix"));
    }

    // AC-B1: the mail brief job of Central's timer still runs on the work machine, with the hook told it runs there.
    [Fact]
    public async Task RunAsync_WorkMachineReadVerbsOnly_RunsWithWorkMailHook()
    {
        // Arrange
        _h.NodePolicy = _h.NodePolicy with { WorkMail = true };
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root, AllowedTools: ["Bash(zyggy outlook *)"]));

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        _h.Request().PreToolUseHook!.Args.Should().Contain("--work-mail");
    }

    // AC-B3: another machine runs the same tools, and its hook gets no --work-mail.
    [Fact]
    public async Task RunAsync_OtherMachineMailAndCodeRules_RunsWithoutWorkMailHook()
    {
        // Arrange
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root, AllowedTools: ["Bash(zyggy outlook *)", "Bash(python *)"]));

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        _h.Request().PreToolUseHook!.Args.Should().NotContain("--work-mail");
    }
}
