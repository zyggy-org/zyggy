using System.Text.RegularExpressions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Serilog.Extensions.Logging;

using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;
using Zyggy.Core.Envelope;
using Zyggy.Core.Jobs;
using Zyggy.Core.Models;
using Zyggy.Core.Policy;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;
using Zyggy.Node;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Node;

/// <summary>Spec 07 AC-13, AC-15, AC-16, AC-21 and AC-22: the host built in process from a <c>node.json</c> — the production types, the Serilog file, the start-up refusals, the exit codes.</summary>
public sealed partial class NodeHostStartupTests : IDisposable
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private readonly ScratchDirectory _scratch = new();

    // ----- plan 07 Step 1: the binary without a loop -----

    [Fact]
    public async Task RunAsync_LogDirectoryNotCreatable_ExitSixStderr()
    {
        string nodeDir = _scratch.File("not-a-directory");
        await File.WriteAllTextAsync(nodeDir, "a file where the node directory should be", TestContext.Current.CancellationToken);
        string path = WriteNodeJson(nodeDir);

        int exitCode = await NodeHost.RunAsync(["--config", path], TestContext.Current.CancellationToken);
        Action build = () => NodeHost.Build(["--config", path]);

        exitCode.Should().Be(6);
        build.Should().Throw<NodeStartupException>().Which.Should().Match<NodeStartupException>(e =>
            e.ExitCode == 6 && e.Message.Contains(Path.Join(nodeDir, "logs"), StringComparison.Ordinal));
    }

    [Fact]
    public void Build_AcceptedConfiguration_RegistersNodeConfigurationAndSerilogLoggerFactory()
    {
        string path = WriteNodeJson(_scratch.File("node"));

        using IHost host = NodeHost.Build(["--config", path]);

        NodeConfiguration configuration = host.Services.GetRequiredService<NodeConfiguration>();
        configuration.Tenant.Should().Be(Acme);
        configuration.Machine.Should().Be(HomeLaptop);
        configuration.SignWith.Should().Be(TestKeys.Acme1);
        host.Services.GetRequiredService<ILoggerFactory>().Should().BeOfType<SerilogLoggerFactory>();
    }

    // ----- plan 07 Step 9: the real host runs the loop -----

    [Fact]
    public async Task Build_ThenStart_ProductionTypesSerilogAndCleanStart()
    {
        // Arrange: AC-13
        var ct = TestContext.Current.CancellationToken;
        await using NodeHostHarness harness = await NodeHostHarness.CreateAsync(ct);
        using IHost host = harness.Build();

        // Act
        await host.StartAsync(ct);
        NodeStatus status = host.Services.GetRequiredService<NodeStatus>();
        await NodeHostHarness.WaitUntilAsync(() => Task.FromResult(status.Listener?.Status.LastPollUtc is not null), TimeSpan.FromSeconds(10), ct);
        await host.StopAsync(ct);

        // Assert
        host.Services.GetRequiredService<IBusProvider>().Should().BeOfType<GitHubBusProvider>();
        host.Services.GetRequiredService<ISecretStore>().Should().BeOfType<FileSecretStore>();
        host.Services.GetRequiredService<IModelRunner>().GetType().Name.Should().Be("ClaudeCodeCliRunner");
        Path.GetFullPath(host.Services.GetRequiredService<IOptions<ClaudeCodeOptions>>().Value.Path).Should().Be(Path.GetFullPath(FakeClaude.ExecutablePath), "node.json carries the path with forward slashes");
        host.Services.GetRequiredService<JobRunner>().Should().NotBeNull();
        host.Services.GetRequiredService<JobHandler>().Should().NotBeNull("the runner's RunAsync, decorated with the login re-probe (plan 07 Step 10)");
        host.Services.GetRequiredService<IPolicySource>().Should().BeOfType<NodeJsonPolicySource>();
        host.Services.GetRequiredService<JobGate>().Should().NotBeNull();
        harness.LogLines.Should().Contain(l => l.Contains("node started: tenant acme, machine home-laptop", StringComparison.Ordinal));
        harness.Head.Requests.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("NotARepository")]
    [InlineData("NoTenantDirectory")]
    [InlineData("KeyMissing")]
    [InlineData("ListenerRunning")]
    public async Task Run_StartupRefusal_ExitSixOneErrorLineNoPollNoHealth(string condition)
    {
        // Arrange: AC-15 — the §5 start-up rule through the host
        var ct = TestContext.Current.CancellationToken;
        await using NodeHostHarness harness = await NodeHostHarness.CreateAsync(ct, seedSecrets: condition != "KeyMissing");
        ServiceProvider? other = null;
        BusRepoFixture? foreign = null;
        switch (condition)
        {
            case "NotARepository":
                harness.WriteNodeJson(busLocalPath: harness.Scratch.File("not-a-repo"));
                Directory.CreateDirectory(harness.Scratch.File("not-a-repo"));
                break;
            case "NoTenantDirectory":
                // a checkout of a bus seeded for another tenant only: no tenants/acme/ in it
                foreign = new BusRepoFixture(TenantId.Parse("globex"));
                await foreign.InitializeAsync();
                await foreign.SeedTenantAsync(TenantId.Parse("globex"), [HomeLaptop], ct);
                harness.WriteNodeJson(busLocalPath: foreign.CloneDir);
                break;
            case "ListenerRunning":
                other = TestHost.BuildListener(harness.Bus, HomeLaptop, harness.Store);
                (await other.GetRequiredService<BusListener>().StartAsync(ct)).Should().Be(BusStartupResult.Clean);
                break;
            default:
                break;
        }

        try
        {
            // Act
            int exitCode = await harness.RunAsync(TimeSpan.FromSeconds(20), ct);

            // Assert
            exitCode.Should().Be(6);
            string[] errors = harness.LogLines.Where(l => l.Contains("[ERR]", StringComparison.Ordinal)).ToArray();
            errors.Should().ContainSingle().Which.Should().Contain(condition).And.Contain("Node: refused to start");
            harness.Head.Requests.Should().BeEmpty();
            (await harness.CommitSubjectsAsync(ct)).Should().NotContain(s => s.StartsWith("acme claim ", StringComparison.Ordinal));
        }
        finally
        {
            other?.GetRequiredService<BusListener>().Dispose();
            if (other is not null)
            {
                await other.DisposeAsync();
            }

            if (foreign is not null)
            {
                await foreign.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task Run_DirtyCheckout_StashedWarningThenPollsAndClaims()
    {
        // Arrange: AC-16 — an uncommitted change under tenants/acme/ in the clone before the start
        var ct = TestContext.Current.CancellationToken;
        await using NodeHostHarness harness = await NodeHostHarness.CreateAsync(ct);
        await File.AppendAllTextAsync(Path.Combine(harness.Bus.CloneDir, "tenants", "acme", "PROTOCOL.md"), "a local edit\n", ct);
        ScratchProject project = await harness.ProjectAsync(ct);
        SeededJob job = await harness.SeedJobAsync(project, ct);

        // Act
        int exitCode = await harness.RunUntilAsync(async () => (await harness.ReportsOnMainAsync(ct)).Count == 1, TimeSpan.FromSeconds(60), ct);

        // Assert
        exitCode.Should().Be(0);
        harness.LogLines.Where(l => l.Contains("[WRN]", StringComparison.Ordinal)).Should().ContainSingle().Which.Should().Contain("zyggy-home-laptop-");
        Model report = await harness.ReportAsync((await harness.ReportsOnMainAsync(ct)).Single(), ct);
        report.Header.InReplyTo.Should().Be(job.Id);
        report.Report!.Status.Should().Be(ReportStatus.Done);
    }

    [Fact]
    public async Task Run_UnexpectedListenerFault_ExitOneNodeFaulted()
    {
        // Arrange: a mode selector that throws stands in for any unexpected fault of the loop
        var ct = TestContext.Current.CancellationToken;
        await using NodeHostHarness harness = await NodeHostHarness.CreateAsync(ct);

        // Act
        PollModeSelector throwing = _ => throw new InvalidOperationException("boom");
        int exitCode = await harness.RunAsync(TimeSpan.FromSeconds(20), ct, s => s.AddSingleton(throwing));

        // Assert
        exitCode.Should().Be(1);
        harness.LogLines.Where(l => l.Contains("[ERR]", StringComparison.Ordinal)).Should().ContainSingle().Which.Should().Contain("node faulted: InvalidOperationException");
    }

    [Fact]
    public async Task Run_OneJob_LogLinesCarryTenantMachinePhaseJobIdAndNoSecret()
    {
        // Arrange: AC-21
        var ct = TestContext.Current.CancellationToken;
        await using NodeHostHarness harness = await NodeHostHarness.CreateAsync(ct);
        ScratchProject project = await harness.ProjectAsync(ct);
        const string token = "body-token-5e1f-do-not-log";
        SeededJob job = await harness.SeedJobAsync(project, ct, body: $"Read README.md ({token}) and finish with a REPORT section; change nothing.\n");

        // Act
        int exitCode = await harness.RunUntilAsync(async () => (await harness.ReportsOnMainAsync(ct)).Count == 1, TimeSpan.FromSeconds(60), ct);

        // Assert
        exitCode.Should().Be(0);
        IReadOnlyList<string> lines = harness.LogLines;
        lines.Should().NotBeEmpty().And.AllSatisfy(l => l.Should().MatchRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z \[(DBG|INF|WRN|ERR)\] acme home-laptop "));
        lines.Where(l => l.Contains("Zyggy.Core.Jobs.JobRunner:", StringComparison.Ordinal)).Should().NotBeEmpty().And.AllSatisfy(l => l.Should().Contain(" run " + job.Id.Value + " "));
        lines.Where(l => l.Contains("Zyggy.Core.Bus.BusListener:", StringComparison.Ordinal)).Should().NotBeEmpty().And.AllSatisfy(l => PhaseOf(l).Should().BeOneOf("poll", "pull", "claim", "report", "reject"));
        SecretSweep.AssertAbsent(lines);
        lines.Should().NotContain(l => l.Contains(token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_QuietTicks_WriteNothingAtInformation()
    {
        // Arrange: AC-21 — the default level, a quiet bus
        var ct = TestContext.Current.CancellationToken;
        await using NodeHostHarness harness = await NodeHostHarness.CreateAsync(ct);
        harness.WriteNodeJson(logLevel: null);

        // Act
        int exitCode = await harness.RunAsync(TimeSpan.FromSeconds(4), ct);

        // Assert: every line is a start-up line; a 304 tick writes nothing
        exitCode.Should().Be(0);
        harness.Head.Requests.Count.Should().BeGreaterThan(1, "several ticks happened");
        harness.LogLines.Should().NotBeEmpty().And.AllSatisfy(l => MessageOf(l).Should().Match(m =>
            m.StartsWith("node started:", StringComparison.Ordinal)
            || m == "polling"
            || m.StartsWith("health endpoint listening", StringComparison.Ordinal)
            || m == "claude auth ok"));
        harness.LogLines.Should().NotContain(l => l.Contains("[DBG]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_LogLevelDebug_YieldsDebugLines()
    {
        // Arrange: AC-21, plan A9
        var ct = TestContext.Current.CancellationToken;
        await using NodeHostHarness harness = await NodeHostHarness.CreateAsync(ct);

        // Act
        int exitCode = await harness.RunAsync(TimeSpan.FromSeconds(3), ct);

        // Assert
        exitCode.Should().Be(0);
        harness.LogLines.Should().Contain(l => l.Contains("[DBG]", StringComparison.Ordinal) && l.Contains("node.json read from", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_PlainProcessCancelled_ExitZeroStoppingLineAbsentWhenNoRun()
    {
        // Arrange: AC-22 (plan A4: in-process cancellation stands for SIGINT)
        var ct = TestContext.Current.CancellationToken;
        await using NodeHostHarness harness = await NodeHostHarness.CreateAsync(ct);

        // Act
        int exitCode = await harness.RunAsync(TimeSpan.FromSeconds(3), ct);

        // Assert
        exitCode.Should().Be(0);
        harness.LogLines.Should().NotContain(l => l.Contains("stopping:", StringComparison.Ordinal));
        File.Exists(Path.Combine(harness.Bus.CloneDir, ".git", "zyggy-listen.lock")).Should().BeFalse("the listen lock is released on stop");
    }

    public void Dispose() => _scratch.Dispose();

    /// <summary>The <c>{phase}</c> column of the output template: the token after <c>acme home-laptop</c>.</summary>
    private static string PhaseOf(string line) => Columns().Match(line).Groups["phase"].Value;

    private static string MessageOf(string line) => line[(line.IndexOf(": ", line.IndexOf("] ", StringComparison.Ordinal), StringComparison.Ordinal) + 2)..];

    [GeneratedRegex(@"^\S+ \[\w{3}\] acme home-laptop (?<phase>\S*) ")]
    private static partial Regex Columns();

    private string WriteNodeJson(string nodeDir) =>
        NodeJson.Write(
            _scratch.File("config"),
            Acme,
            HomeLaptop,
            TestKeys.Acme1,
            busLocalPath: _scratch.File("bus"),
            remote: "https://example.invalid/acme-org/acme-bus.git",
            secretsRoot: _scratch.File("secrets"),
            nodeDir: nodeDir);
}
