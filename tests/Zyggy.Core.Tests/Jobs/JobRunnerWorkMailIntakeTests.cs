using Zyggy.Core.Envelope;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Jobs;

/// <summary>
/// Spec 58 decision B as corrected by the owner (2026-11-23): enabling Outlook on a work machine (<c>outlook.enabled</c> true) does not
/// affect other work and refuses no job for its rules. A code job runs exactly as before; a <b>mail job</b> (its <c>allowed_tools</c> name
/// <c>zyggy outlook</c>) runs with the read verbs and the file reads only. Separation is kept at run time: the hook learns it runs on a
/// work machine (<c>--work-mail</c>), and a job that is not a mail job gets no work mail marker, so the outlook read tools refuse it.
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

    // AC-17: a mail job that also holds code rules is not refused; it runs with its read verbs and file reads only, model and hook alike.
    [Theory]
    [InlineData("Edit")]
    [InlineData("Bash")]
    [InlineData("Bash(python *)")]
    public async Task RunAsync_WorkMachineMailJobWithCode_RunsWithTheReadRulesOnly(string extra)
    {
        // Arrange
        _h.NodePolicy = _h.NodePolicy with { WorkMail = true };
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root, AllowedTools: ["Read", "Bash(zyggy outlook *)", extra]));

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        var request = _h.Request();
        (string.Join('|', request.AllowedTools!), request.PreToolUseHook!.Args.Contains(extra))
            .Should().Be(("Read|Bash(zyggy outlook *)", false));
    }

    // AC-17: a mail job whose only outlook rule is dropped (a send rule) runs with the file reads, never with wider tools.
    [Fact]
    public async Task RunAsync_WorkMachineMailJobWithOnlyASendRule_RunsWithFileReads()
    {
        // Arrange
        _h.NodePolicy = _h.NodePolicy with { WorkMail = true };
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root, AllowedTools: ["Bash(zyggy outlook send *)"]));

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        _h.Request().AllowedTools.Should().Equal("Read", "Grep", "Glob");
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
