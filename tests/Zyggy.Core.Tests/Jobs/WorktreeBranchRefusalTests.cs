using System.Text;

using NSubstitute;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Processes;
using Zyggy.Core.Registry;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Core.Tests.Registry;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Jobs;

/// <summary>
/// Spec 53 slice 2: the node refuses a named branch it must not touch — on a mandate job (<c>base</c> or <c>mandate</c> key) or when it
/// is the project's default branch (git's name or the node's registry entry, any letter case) — with <c>rejected</c> /
/// <c>branch_refused</c>, before the model and before any writing git command (git and model substituted).
/// </summary>
// AC-8a, AC-8b, AC-9c; AC-14 (unit) for the two refusal rows.
public sealed class WorktreeBranchRefusalTests : IDisposable
{
    private const string MandateId = "01M7N1H4JFP3VJE7RM26YA6XHM";

    // The only git commands a refusal may run after pre-flight: the origin config read and ref resolution (AC-8a).
    private static readonly string[] ReadOnlyGit = ["config", "rev-parse", "symbolic-ref"];

    private readonly ScratchProject _project = ScratchProject.Create();

    private readonly JobRunnerHarness _h = new();

    private readonly WorktreeGit _git;

    public WorktreeBranchRefusalTests() => _git = new WorktreeGit(_h.Processes);

    public void Dispose() => _project.Dispose();

    private Task<Model> NamedJobAsync(string branch, string? project = null) =>
        JobRunnerHarness.JobAsync(new JobFields(project ?? _project.Root, Worktree: true, Branch: branch));

    // A job carrying a key this node does not interpret (§4 unknown fields: preserved, never interpreted) — spec 18's base or mandate.
    private static async Task<Model> WithKeyAsync(JobFields fields, string key, string value)
    {
        var job = await JobRunnerHarness.JobAsync(fields);
        var text = Encoding.UTF8.GetString(EnvelopeWriter.Serialize(job));
        text.Should().Contain("worktree: true\n");
        var parsed = EnvelopeParser.Parse(Encoding.UTF8.GetBytes(text.Replace("worktree: true\n", "worktree: true\n" + key + ": " + value + "\n", StringComparison.Ordinal)), job.Header.Tenant);
        parsed.IsAccepted.Should().BeTrue(parsed.Rejection?.ToString());
        return parsed.Envelope!;
    }

    private void RegistryEntry(string name, string? defaultBranch) =>
        _h.Registry.Replace(RegistryDocumentTests.Central() with
        {
            Projects = [new RegistryProject(name, _project.Root.Replace('\\', '/'), defaultBranch, [], [], null)],
        });

    // Every git command except pre-flight's own `rev-parse --git-dir` (the hook self-test is not git).
    private List<ProcessSpec> GitAfterPreFlight() =>
        [.. _h.Processes.Calls.Where(c => c.FileName != _h.Options.HookExecutable && !c.Arguments.Contains("--git-dir"))];

    private static string Body(JobReply reply) => Encoding.UTF8.GetString(reply.Body.Span);

    private int ModelCalls() => _h.Model.ReceivedCalls().Count();

    // AC-8a: git's default branch, in any letter case.
    [Theory]
    [InlineData("main")]
    [InlineData("Main")]
    [InlineData("MAIN")]
    public async Task RunAsync_BranchIsGitDefaultInAnyCase_RejectedBranchRefusedNamingDefault(string branch)
    {
        // Arrange
        var job = await NamedJobAsync(branch);

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        (reply.Status, reply.Reason, Body(reply)).Should().Be((ReportStatus.Rejected, "branch_refused", "branch_refused: default branch main\n"));
    }

    [Fact]
    public async Task RunAsync_BranchIsGitDefault_ModelNeverCalled()
    {
        // Arrange
        var job = await NamedJobAsync("Main");

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        ModelCalls().Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_BranchIsGitDefault_OnlyReadOnlyGitAfterPreFlight()
    {
        // Arrange
        var job = await NamedJobAsync("Main");

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        GitAfterPreFlight().Select(RecordingProcessRunner.SubCommand).Should().NotBeEmpty().And.OnlyContain(s => ReadOnlyGit.Contains(s));
    }

    [Fact]
    public async Task RunAsync_BranchIsGitDefault_CheckedBeforeTheFetch()
    {
        // Arrange
        var job = await NamedJobAsync("main");

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        _h.Processes.CallsOf("fetch").Should().BeEmpty();
    }

    // AC-8b: the node's registry entry names another default than git.
    [Theory]
    [InlineData("develop")]
    [InlineData("Develop")]
    public async Task RunAsync_BranchIsRegistryDefault_RejectedBranchRefusedNamingIt(string branch)
    {
        // Arrange
        _git.OriginHeadBranch = "feature-x";
        RegistryEntry("shop", "develop");
        var job = await NamedJobAsync(branch, "shop");

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        (reply.Status, reply.Reason, Body(reply)).Should().Be((ReportStatus.Rejected, "branch_refused", "branch_refused: default branch develop\n"));
    }

    [Fact]
    public async Task RunAsync_BranchIsRegistryDefault_ModelNeverCalledAndNoWritingGit()
    {
        // Arrange
        _git.OriginHeadBranch = "feature-x";
        RegistryEntry("shop", "develop");
        var job = await NamedJobAsync("develop", "shop");

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        (ModelCalls(), GitAfterPreFlight().TrueForAll(c => ReadOnlyGit.Contains(RecordingProcessRunner.SubCommand(c)))).Should().Be((0, true));
    }

    [Fact]
    public async Task RunAsync_RegistryEntryPresentBranchIsGitDefault_StillRefusedNamingGitDefault()
    {
        // Arrange
        _git.OriginHeadBranch = "feature-x";
        RegistryEntry("shop", "develop");
        var job = await NamedJobAsync("feature-x", "shop");

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        (reply.Status, Body(reply)).Should().Be((ReportStatus.Rejected, "branch_refused: default branch feature-x\n"));
    }

    [Fact]
    public async Task RunAsync_PathGivenProjectEmptySnapshot_RegistryNameNotRefused()
    {
        // Arrange
        _git.OriginHeadBranch = "feature-x";
        var job = await NamedJobAsync("develop");

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        (reply.Status, ModelCalls()).Should().Be((ReportStatus.Done, 1));
    }

    [Fact]
    public async Task RunAsync_PathGivenProjectWhileSnapshotHasEntry_RegistryNameNotRefused()
    {
        // Arrange: the entry belongs to the name "shop"; a job naming the folder by path is not looked up.
        _git.OriginHeadBranch = "feature-x";
        RegistryEntry("shop", "develop");
        var job = await NamedJobAsync("develop");

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        reply.Status.Should().Be(ReportStatus.Done);
    }

    [Fact]
    public async Task RunAsync_RegistryEntryWithoutDefaultBranch_OnlyGitNameRefused()
    {
        // Arrange
        _git.OriginHeadBranch = "feature-x";
        RegistryEntry("shop", null);
        var job = await NamedJobAsync("develop", "shop");

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        reply.Status.Should().Be(ReportStatus.Done);
    }

    [Fact]
    public async Task RunAsync_UnnamedJobOnRegistryDefault_RunsAsToday()
    {
        // Arrange: no branch, so neither name is checked (AC-12).
        RegistryEntry("shop", "develop");
        var job = await JobRunnerHarness.JobAsync(new JobFields("shop", Worktree: true));

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        reply.Status.Should().Be(ReportStatus.Done);
    }

    // AC-9c: branch together with spec 18's base or mandate.
    [Theory]
    [InlineData("base", "feature/12-cart")]
    [InlineData("mandate", MandateId)]
    public async Task RunAsync_BranchWithMandateKey_RejectedBranchRefused(string key, string value)
    {
        // Arrange
        var job = await WithKeyAsync(new JobFields(_project.Root, Worktree: true, Branch: "feature/12-cart"), key, value);

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        (reply.Status, reply.Reason, Body(reply)).Should().Be((ReportStatus.Rejected, "branch_refused", "branch_refused: branch with base or mandate\n"));
    }

    [Theory]
    [InlineData("base", "feature/12-cart")]
    [InlineData("mandate", MandateId)]
    public async Task RunAsync_BranchWithMandateKey_NoGitAfterPreFlightAndModelNeverCalled(string key, string value)
    {
        // Arrange
        var job = await WithKeyAsync(new JobFields(_project.Root, Worktree: true, Branch: "feature/12-cart"), key, value);

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        (GitAfterPreFlight().Count, ModelCalls()).Should().Be((0, 0));
    }

    [Fact]
    public async Task RunAsync_BranchIsDefaultAndCarriesMandate_MandateRuleComesFirst()
    {
        // Arrange
        var job = await WithKeyAsync(new JobFields(_project.Root, Worktree: true, Branch: "main"), "mandate", MandateId);

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        Body(reply).Should().Be("branch_refused: branch with base or mandate\n");
    }

    [Theory]
    [InlineData("base", "main")]
    [InlineData("mandate", MandateId)]
    public async Task RunAsync_DescriptionWithMandateKeyNoBranch_NotRefused(string key, string value)
    {
        // Arrange
        var job = await WithKeyAsync(new JobFields(_project.Root, Worktree: true, Description: "Cart: slice 2"), key, value);

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        (reply.Status, ModelCalls()).Should().Be((ReportStatus.Done, 1));
    }

    [Fact]
    public async Task RunAsync_NamedJobNotDefault_RunsTheModel()
    {
        // Arrange
        var job = await NamedJobAsync("feature/12-cart");

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        (reply.Status, ModelCalls()).Should().Be((ReportStatus.Done, 1));
    }
}
