using System.Text;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;
using Zyggy.Core.Envelope;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Jobs;

/// <summary>
/// Spec 15 slice 2 (AC-8, AC-9): jobs through the real listener and job runner, <c>tools/fake-claude</c> sending scripted tool calls
/// through the <c>PreToolUse</c> hook of its <c>--settings</c>, and the real <c>zyggy hook pre-tool-use</c> process deciding them.
/// </summary>
public sealed class JobHookRoundTripTests : IAsyncLifetime
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName Central = MachineName.Parse("central");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly string[] ReadWrite = ["Read", "Write"];

    private readonly BusRepoFixture _bus = new(Acme);

    private readonly ScratchDirectory _scratch = new();

    private InMemorySecretStore _store = null!;

    private WorktreeProject _project = null!;

    private string ToolLog => _scratch.File("tools.log");

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await _bus.InitializeAsync();
        await _bus.SeedTenantAsync(Acme, [Central, HomeLaptop], ct);
        _store = await TestHost.StoreAsync(Acme, ct);
        _project = await WorktreeProject.CreateAsync(_bus, ct);
    }

    public async ValueTask DisposeAsync()
    {
        _project?.Dispose();
        _scratch.Dispose();
        await _bus.DisposeAsync();
    }

    private static string Write(string path, string content) =>
        JsonSerializer.Serialize(new { tool_name = "Write", tool_input = new { file_path = path, content } });

    [Fact]
    public async Task ProcessOnceAsync_WriteOutsideAndInside_OutsideRefusedAndAbsentInsideWrittenReportCountsOne()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var outside = _scratch.File("escape.txt");
        await using var services = Build([Write(outside, "x"), Write("notes.txt", "kept")]);
        await SeedAndPullAsync(services, worktree: false, ct);

        // Act
        await ProcessAsync(services, ct);

        // Assert
        var report = await SingleReportAsync(ct);
        new
        {
            report.Report!.Status,
            BlockedLine = BodyOf(report).Split('\n').Single(l => l.StartsWith("blocked:", StringComparison.Ordinal)),
            OutsideWritten = File.Exists(outside),
            Inside = await File.ReadAllTextAsync(Path.Combine(_project.Path, "notes.txt"), ct),
            Log = await File.ReadAllTextAsync(ToolLog, ct),
        }.Should().BeEquivalentTo(new
        {
            Status = ReportStatus.Done,
            BlockedLine = "blocked: 1 tool call(s) outside this job's rules (first: Write: write outside the project)",
            OutsideWritten = false,
            Inside = "kept",
            Log = "blocked Write\nallowed Write\n",
        });
    }

    [Fact]
    public async Task ProcessOnceAsync_ToolNotInAllowedTools_RefusedNotInAllowedTools()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var bash = JsonSerializer.Serialize(new { tool_name = "Bash", tool_input = new { command = "curl https://example.invalid" } });
        await using var services = Build([bash]);
        await SeedAndPullAsync(services, worktree: false, ct);

        // Act
        await ProcessAsync(services, ct);

        // Assert
        BodyOf(await SingleReportAsync(ct)).Should().EndWith("\n\nblocked: 1 tool call(s) outside this job's rules (first: Bash: not in allowed_tools)\n");
    }

    [Fact]
    public async Task ProcessOnceAsync_WorktreeJobWritesIntoTheProjectFolder_RefusedProjectUntouched()
    {
        // Arrange: the worktree is the run directory, so the project folder itself is outside it
        var ct = TestContext.Current.CancellationToken;
        var intoProject = Path.Combine(_project.Path, "from-worktree.txt");
        await using var services = Build([Write(intoProject, "x")]);
        await SeedAndPullAsync(services, worktree: true, ct);

        // Act
        await ProcessAsync(services, ct);

        // Assert
        new { Written = File.Exists(intoProject), Ending = BodyOf(await SingleReportAsync(ct)).Split('\n')[^2] }.Should().BeEquivalentTo(new
        {
            Written = false,
            Ending = "blocked: 1 tool call(s) outside this job's rules (first: Write: write outside the project)",
        });
    }

    [Fact]
    public async Task ProcessOnceAsync_NoRefusals_NoBlockedLine()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var services = Build([Write("notes.txt", "kept")]);
        await SeedAndPullAsync(services, worktree: false, ct);

        // Act
        await ProcessAsync(services, ct);

        // Assert
        var report = await SingleReportAsync(ct);
        new { report.Report!.Status, HasBlocked = BodyOf(report).Contains("blocked:", StringComparison.Ordinal) }
            .Should().BeEquivalentTo(new { Status = ReportStatus.Done, HasBlocked = false });
    }

    [Fact]
    public async Task ProcessOnceAsync_RefusalAndFailingRun_StatusIsTheRunsFailed()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var services = Build([Write(_scratch.File("escape.txt"), "x")], exitCode: 1);
        await SeedAndPullAsync(services, worktree: false, ct);

        // Act
        await ProcessAsync(services, ct);

        // Assert
        var report = await SingleReportAsync(ct);
        new { report.Report!.Status, report.Report.Reason, Blocked = BodyOf(report).Contains("\nblocked: 1 tool call(s)", StringComparison.Ordinal) }
            .Should().BeEquivalentTo(new { Status = ReportStatus.Failed, Reason = "claude_error", Blocked = true });
    }

    [Fact]
    public async Task ProcessOnceAsync_HookBinaryMissing_FailedHookUnavailableNoToolCallMade()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var missing = _scratch.File("no-such-zyggy");
        await using var services = Build([Write("notes.txt", "kept")], hookExecutable: missing);
        await SeedAndPullAsync(services, worktree: false, ct);

        // Act
        await ProcessAsync(services, ct);

        // Assert
        var report = await SingleReportAsync(ct);
        new { report.Report!.Status, report.Report.Reason, Body = BodyOf(report), FakeRan = File.Exists(ToolLog) }.Should().BeEquivalentTo(new
        {
            Status = ReportStatus.Failed,
            Reason = "claude_error",
            Body = $"claude_error: hook unavailable: {missing}\n",
            FakeRan = false,
        });
    }

    private static string BodyOf(Model report) => Encoding.UTF8.GetString(report.Body.Span);

    private static async Task ProcessAsync(ServiceProvider services, CancellationToken ct) =>
        (await services.GetRequiredService<BusListener>().ProcessOnceAsync(ct)).Should().Be(1);

    private ServiceProvider Build(IReadOnlyList<string> toolCalls, int exitCode = 0, string? hookExecutable = null)
    {
        var environment = new Dictionary<string, string>
        {
            ["ZYGGY_FAKE_CLAUDE_SCENARIO"] = exitCode == 0 ? "done" : "error",
            ["ZYGGY_FAKE_CLAUDE_TOOL_CALLS"] = string.Join('\n', toolCalls),
            ["ZYGGY_FAKE_CLAUDE_TOOL_LOG"] = ToolLog,
            ["ZYGGY_FAKE_CLAUDE_EXIT"] = exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        NodeConfiguration configuration = TestHost.NodeConfigurationFor(_bus, HomeLaptop);
        return TestHost.BuildRunnerListener(_bus, HomeLaptop, _store, FakeClaude.ExecutablePath, configuration, o =>
        {
            o.NodeDir = _project.NodeDir;
            o.TimeoutOverride = TimeSpan.FromSeconds(60);
            o.ModelEnvironment = environment;
            o.HookExecutable = hookExecutable;
        });
    }

    private async Task SeedAndPullAsync(ServiceProvider services, bool worktree, CancellationToken ct)
    {
        (await services.GetRequiredService<BusListener>().StartAsync(ct)).Should().Be(BusStartupResult.Clean);
        var fields = new JobFields(_project.Path, Worktree: worktree, TimeoutMinutes: 2, AllowedTools: ReadWrite);
        await _bus.SeedSignedJobAsync(Central, HomeLaptop, TestKeys.Acme1, _store, "Change the shop.\n", fields: fields, ct: ct);
        (await services.GetRequiredService<IBusProvider>().PullAsync(ct)).Succeeded.Should().BeTrue();
    }

    private async Task<Model> SingleReportAsync(CancellationToken ct)
    {
        string output = await _bus.RunGitAsync(_bus.BareDir, ["ls-tree", "--name-only", "main", BusPaths.Reports(Acme, HomeLaptop) + "/"], ct);
        string path = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(p => p.EndsWith(".md", StringComparison.Ordinal)).Should().ContainSingle().Subject;
        EnvelopeResult parsed = await new EnvelopeSigner(_store).ParseAndVerifyAsync(await _bus.ShowBytesAsync(path, ct), Acme, ct);
        parsed.IsAccepted.Should().BeTrue(parsed.Rejection?.ToString());
        return parsed.Envelope!;
    }
}
