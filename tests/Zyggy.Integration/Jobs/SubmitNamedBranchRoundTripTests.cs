using System.Text.RegularExpressions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;
using Zyggy.Core.Envelope;
using Zyggy.Core.Jobs;
using Zyggy.Integration.Cli;
using Zyggy.Integration.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Jobs;

/// <summary>
/// Spec 53 slice 7, AC-16: the owner asks for a named branch with <c>zyggy submit --worktree --branch --description</c> (the <c>zyggy</c>
/// verb in process, from <c>central</c>'s checkout) and gets it back through the production node composition root on <c>home-laptop</c>,
/// real <c>git</c>, a project whose <c>origin</c> is a bare repository and <c>tools/fake-claude</c>: two jobs in a row produce one branch
/// on the origin with both jobs' commits and two <c>done</c> reports naming it; the owner's checkout is unchanged and no local branch of
/// that name is created.
/// </summary>
public sealed partial class SubmitNamedBranchRoundTripTests : IAsyncLifetime
{
    private const string Cart = "feature/12-cart";

    private CliHarness _harness = null!;

    private WorktreeProject _project = null!;

    private readonly ScratchDirectory _scratch = new();

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        _harness = await CliHarness.CreateAsync(ct);
        _project = await WorktreeProject.CreateAsync(_harness.Bus, ct);
    }

    public async ValueTask DisposeAsync()
    {
        _project?.Dispose();
        _scratch.Dispose();
        await _harness.DisposeAsync();
    }

    // AC-16: two submits with the same --branch, run by the production host, give one branch on origin with both commits and two done reports.
    [Fact]
    public async Task SubmitTwiceSameBranch_ProductionHost_OneOriginBranchBothCommitsTwoDoneReportsCheckoutUntouched()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string checkoutBefore = await CheckoutStateAsync(ct);
        await using ServiceProvider services = await StartNodeAsync("+src/cart.txt\tcart", ct);

        // Act
        EnvelopeId first = await SubmitAndRunAsync(services, "Cart: slice 1", ct);
        WithEdits(services, "+src/totals.txt\ttotals");
        EnvelopeId second = await SubmitAndRunAsync(services, "Cart: slice 2", ct);

        // Assert
        Model firstReport = await ReportForAsync(first, ct);
        Model secondReport = await ReportForAsync(second, ct);
        new
        {
            Subjects = await _project.OriginGitAsync(["log", "--format=%s", "main..refs/heads/" + Cart], ct),
            OriginTip = await _project.ResolveAsync(_project.OriginDir, "refs/heads/" + Cart, ct),
            Statuses = new[] { firstReport.Report!.Status, secondReport.Report!.Status },
            FirstNamesBranch = firstReport.BodyText.Contains("\nbranch " + Cart + " pushed at " + firstReport.Report.DiffRef + "\n", StringComparison.Ordinal),
            SecondNamesBranch = secondReport.BodyText.Contains("\nbranch " + Cart + " pushed at " + secondReport.Report.DiffRef + "\n", StringComparison.Ordinal),
            Checkout = await CheckoutStateAsync(ct),
            LocalNamed = await _project.GitAsync(["for-each-ref", "--format=%(refname)", "refs/heads/" + Cart], ct),
        }.Should().BeEquivalentTo(new
        {
            Subjects = "Cart: slice 2\nCart: slice 1",
            OriginTip = secondReport.Report.DiffRef,
            Statuses = new[] { ReportStatus.Done, ReportStatus.Done },
            FirstNamesBranch = true,
            SecondNamesBranch = true,
            Checkout = checkoutBefore,
            LocalNamed = string.Empty,
        });
    }

    // fake-claude reads its edits from the model environment of each run, so the second job of the same host can change them.
    private static void WithEdits(ServiceProvider services, string edits)
    {
        var options = services.GetRequiredService<IOptions<JobRunnerOptions>>().Value;
        options.ModelEnvironment = new Dictionary<string, string>(options.ModelEnvironment) { ["ZYGGY_FAKE_CLAUDE_EDITS"] = edits };
    }

    private async Task<ServiceProvider> StartNodeAsync(string edits, CancellationToken ct)
    {
        var environment = new Dictionary<string, string>
        {
            ["ZYGGY_FAKE_CLAUDE_SCENARIO"] = "done",
            ["ZYGGY_FAKE_CLAUDE_CAPTURE"] = _scratch.File("capture.bin"),
            ["ZYGGY_FAKE_CLAUDE_EDITS"] = edits,
        };
        NodeConfiguration configuration = TestHost.NodeConfigurationFor(_harness.Bus, CliHarness.HomeLaptop);
        var services = TestHost.BuildRunnerListener(_harness.Bus, CliHarness.HomeLaptop, _harness.Store, FakeClaude.ExecutablePath, configuration, o =>
        {
            o.NodeDir = _project.NodeDir;
            o.TimeoutOverride = TimeSpan.FromSeconds(30);
            o.ModelEnvironment = environment;
        });
        (await services.GetRequiredService<BusListener>().StartAsync(ct)).Should().Be(BusStartupResult.Clean);
        return services;
    }

    // The owner's command on central, then home-laptop's listener pulls and runs the job once.
    private async Task<EnvelopeId> SubmitAndRunAsync(ServiceProvider services, string description, CancellationToken ct)
    {
        var (exit, console) = await _harness.RunAsync(
            ["submit", "--to", "home-laptop", "--project", _project.Path, "--worktree", "--branch", Cart, "--description", description, "--allowed-tools", "Read,Edit", "--timeout", "2", "Change the shop.\n"],
            _harness.CentralNodeJson,
            ct: ct);
        exit.Should().Be(0, console.Stderr);
        Match line = SubmittedLine().Match(console.Stdout);
        line.Success.Should().BeTrue(console.Stdout);
        (await services.GetRequiredService<IBusProvider>().PullAsync(ct)).Succeeded.Should().BeTrue();
        (await services.GetRequiredService<BusListener>().ProcessOnceAsync(ct)).Should().Be(1);
        return EnvelopeId.Parse(line.Groups["id"].Value);
    }

    // The owner's checkout: current branch, HEAD, local branches and working-tree status.
    private async Task<string> CheckoutStateAsync(CancellationToken ct) =>
        await _project.GitAsync(["symbolic-ref", "HEAD"], ct)
        + await _project.GitAsync(["rev-parse", "HEAD"], ct)
        + await _project.GitAsync(["for-each-ref", "--format=%(refname) %(objectname)", "refs/heads/"], ct)
        + await _project.GitAsync(["status", "--porcelain"], ct);

    private async Task<Model> ReportForAsync(EnvelopeId job, CancellationToken ct)
    {
        string output = await _harness.Bus.RunGitAsync(_harness.Bus.BareDir, ["ls-tree", "--name-only", "main", BusPaths.Reports(CliHarness.Acme, CliHarness.HomeLaptop) + "/"], ct);
        var reports = new List<Model>();
        foreach (var path in output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(p => p.EndsWith(".md", StringComparison.Ordinal)))
        {
            EnvelopeResult parsed = await new EnvelopeSigner(_harness.Store).ParseAndVerifyAsync(await _harness.Bus.ShowBytesAsync(path, ct), CliHarness.Acme, ct);
            parsed.IsAccepted.Should().BeTrue(parsed.Rejection?.ToString());
            reports.Add(parsed.Envelope!);
        }

        return reports.Where(r => r.Header.InReplyTo == job).Should().ContainSingle().Subject;
    }

    [GeneratedRegex(@"^submitted (?<id>[0-9A-HJKMNP-TV-Z]{26}) to (?<to>[a-z0-9-]+) \((?<path>\S+)\)")]
    private static partial Regex SubmittedLine();
}
