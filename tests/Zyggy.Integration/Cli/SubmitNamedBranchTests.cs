using System.Text.RegularExpressions;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Registry;
using Zyggy.Integration.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Cli;

/// <summary>
/// Spec 53 slice 7, AC-15: <c>zyggy submit --worktree --branch &lt;name&gt; --description &lt;text&gt;</c> in process from <c>central</c>'s
/// <c>node.json</c> against the local bare bus — a signed job <c>zyggy verify</c> accepts; <c>--branch</c> without <c>--worktree</c>, a
/// malformed name or description, and the target project's registry default branch exit 2 and write nothing; without a registry entry
/// for the target project the job is sent and the node decides.
/// </summary>
public sealed partial class SubmitNamedBranchTests : IAsyncLifetime
{
    private const string Body = "Build the cart totals.\n";

    private const string Cart = "feature/12-cart";

    private CliHarness _harness = null!;

    private ScratchProject _project = null!;

    public async ValueTask InitializeAsync()
    {
        _harness = await CliHarness.CreateAsync(CancellationToken.None);
        _project = await ScratchProject.CreateAsync(_harness.Bus, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        _project.Dispose();
        await _harness.DisposeAsync();
    }

    // AC-15: both options give a signed job carrying both fields, the description with YAML-special characters unchanged.
    [Fact]
    public async Task Submit_WorktreeBranchDescription_JobOnMainCarriesBothFields()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        const string description = "Cart, slice 2: totals # \"quoted\" 'too'";

        // Act
        var (exit, console) = await _harness.RunAsync(["submit", "--to", "home-laptop", "--project", _project.Path, "--worktree", "--branch", Cart, "--description", description, Body], _harness.CentralNodeJson, ct: ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        Model job = await _harness.ParseOnMainAsync(JobPath(console.Stdout), ct);
        new { job.Job!.Worktree, job.Job.Branch, job.Job.Description }.Should().BeEquivalentTo(new { Worktree = true, Branch = Cart, Description = description });
    }

    // AC-15: the job written with --branch and --description is accepted by zyggy verify on the receiving machine's checkout.
    [Fact]
    public async Task Submit_WorktreeBranchDescription_ZyggyVerifyAcceptsTheJob()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (submitted, submitConsole) = await _harness.RunAsync(["submit", "--to", "home-laptop", "--project", _project.Path, "--worktree", "--branch", Cart, "--description", "Cart: slice 1", Body], _harness.CentralNodeJson, ct: ct);
        submitted.Should().Be(0, submitConsole.Stderr);
        await _harness.Bus.PullCloneAsync(ct);

        // Act
        var (exit, console) = await _harness.RunAsync(["verify", "--verbose"], _harness.HomeLaptopNodeJson, ct: ct);

        // Assert
        string[] lines = Lines(console.Stdout);
        new { Exit = exit, NoFailure = lines[^1].Contains(" ok, 0 failed,", StringComparison.Ordinal), JobOk = lines.Contains("OK " + JobPath(submitConsole.Stdout)) }
            .Should().BeEquivalentTo(new { Exit = 0, NoFailure = true, JobOk = true }, console.Stdout + console.Stderr);
    }

    // AC-15: --branch without --worktree, a malformed name and a malformed description each exit 2 with one line and write nothing.
    [Theory]
    [InlineData("--branch", Cart, false, "branch")]
    [InlineData("--branch", "zyggy/x", true, "branch")]
    [InlineData("--branch", "a..b", true, "branch")]
    [InlineData("--description", "line one\nline two", true, "description")]
    public async Task Submit_RefusedOption_ExitTwoOneLineNothingWritten(string option, string value, bool worktree, string field)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        int commits = await _harness.CommitCountOnMainAsync(ct);
        string[] args = worktree
            ? ["submit", "--to", "home-laptop", "--project", _project.Path, "--worktree", option, value, Body]
            : ["submit", "--to", "home-laptop", "--project", _project.Path, option, value, Body];

        // Act
        var (exit, console) = await _harness.RunAsync(args, _harness.CentralNodeJson, ct: ct);

        // Assert
        new
        {
            Exit = exit,
            OneFieldLine = Lines(console.Stderr) is [var only] && only.StartsWith("zyggy: " + field + ":", StringComparison.Ordinal),
            console.Stdout,
            Commits = await _harness.CommitCountOnMainAsync(ct),
            Porcelain = await _harness.PorcelainAsync(_harness.Bus.SenderDir, ct),
        }.Should().BeEquivalentTo(new { Exit = 2, OneFieldLine = true, Stdout = string.Empty, Commits = commits, Porcelain = string.Empty });
    }

    // AC-15: the target project's default branch in the target machine's registry file is refused, in any letter case, before signing.
    [Theory]
    [InlineData("main")]
    [InlineData("Main")]
    public async Task Submit_BranchIsTargetProjectsRegistryDefault_ExitTwoNothingWritten(string branch)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await SeedRegistryAsync(new RegistryProject("shop", "/home/alice/src/shop", "main", [], [], null), ct);
        int commits = await _harness.CommitCountOnMainAsync(ct);

        // Act
        var (exit, console) = await _harness.RunAsync(["submit", "--to", "home-laptop", "--project", "shop", "--worktree", "--branch", branch, Body], _harness.CentralNodeJson, ct: ct);

        // Assert
        new
        {
            Exit = exit,
            console.Stderr,
            console.Stdout,
            Commits = await _harness.CommitCountOnMainAsync(ct),
        }.Should().BeEquivalentTo(new
        {
            Exit = 2,
            Stderr = "zyggy: branch: " + branch + " is the default branch main of project shop on home-laptop\n",
            Stdout = string.Empty,
            Commits = commits,
        });
    }

    // AC-15: with no registry entry for the target project the job is sent, and the node decides at run time.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Submit_NoRegistryEntryForProject_JobSentWithBranch(bool otherProjectRegistered)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        if (otherProjectRegistered)
        {
            await SeedRegistryAsync(new RegistryProject("calizr", "/home/alice/src/calizr", "main", [], [], null), ct);
        }

        // Act
        var (exit, console) = await _harness.RunAsync(["submit", "--to", "home-laptop", "--project", "shop", "--worktree", "--branch", "main", Body], _harness.CentralNodeJson, ct: ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        (await _harness.ParseOnMainAsync(JobPath(console.Stdout), ct)).Job!.Branch.Should().Be("main");
    }

    private static string JobPath(string stdout)
    {
        Match line = SubmittedLine().Match(stdout);
        line.Success.Should().BeTrue(stdout);
        return BusPaths.EnvelopeFile(BusPaths.Jobs(CliHarness.Acme, CliHarness.HomeLaptop), EnvelopeId.Parse(line.Groups["id"].Value));
    }

    private static string[] Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    // home-laptop publishes its registry file; central's checkout pulls it, as its listener would.
    private async Task SeedRegistryAsync(RegistryProject project, CancellationToken ct)
    {
        var document = new RegistryDocument(CliHarness.Acme, CliHarness.HomeLaptop, RegistryOs.Linux, null, null, null, [project]);
        await _harness.Bus.SeedRawFileAsync(BusPaths.Registry(CliHarness.Acme, CliHarness.HomeLaptop), document.Serialize(), "acme registry home-laptop", ct);
        await _harness.Bus.RunGitAsync(_harness.Bus.SenderDir, ["pull", "-q", "--rebase"], ct);
    }

    [GeneratedRegex(@"^submitted (?<id>[0-9A-HJKMNP-TV-Z]{26}) to (?<to>[a-z0-9-]+) \((?<path>\S+)\)")]
    private static partial Regex SubmittedLine();
}
