using System.Text;

using NSubstitute;

using Zyggy.Core.Envelope;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Jobs;

/// <summary>
/// Spec 58 decision B (2026-11-22): on a work machine (<c>outlook.enabled</c> true) the runner refuses, before the hook, the lock and the
/// model, a job whose effective <c>allowed_tools</c> can both read work mail and run code (<c>rejected</c> / <c>work_mail_code_mix</c>);
/// the same job on another machine runs. The hook of a work machine's job learns it runs there (<c>--work-mail</c>).
/// </summary>
public sealed class JobRunnerWorkMailIntakeTests : IDisposable
{
    private readonly ScratchProject _project = ScratchProject.Create();

    private readonly JobRunnerHarness _h = new();

    public void Dispose() => _project.Dispose();

    // AC-B1: refused at intake on a work machine, the model never started, no lock left behind.
    [Fact]
    public async Task RunAsync_WorkMachineMailAndCodeRules_RejectedWorkMailCodeMixModelNeverStarted()
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

    // AC-B1: a job that names no tools gets the node's default, and that default is checked too.
    [Fact]
    public async Task RunAsync_WorkMachineNodeDefaultMixes_Rejected()
    {
        // Arrange
        _h.NodePolicy = _h.NodePolicy with { WorkMail = true, DefaultAllowedTools = ["Read", "Bash"] };
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root));

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
