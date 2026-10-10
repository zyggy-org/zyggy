using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Zyggy.Core.Dream;
using Zyggy.Core.Git;
using Zyggy.Core.Models;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Dream;

/// <summary>AC-19, AC-20 (unit): preflight, one commit of the run's paths, push with one fetch-and-rebase retry, never a force push.</summary>
public sealed partial class DreamRunnerGitTests : IDisposable
{
    private const string Rejected = " ! [rejected]        HEAD -> main (fetch first)\nerror: failed to push some refs";

    private readonly MemoryTree _tree = new(("inbox/m365-mail-backfill-2026-09-29.md",
        "- [observed] 2026-09-29 [m365-mail 2026-09-29]: mail fact 1.\n- [observed] 2026-09-29 [m365-mail 2026-09-29]: mail fact 2.\n"));

    private readonly IModelRunner _model = Substitute.For<IModelRunner>();
    private readonly RecordingProcessRunner _git = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 4, 1, 0, 0, TimeSpan.Zero));
    private readonly List<string> _events = [];

    public DreamRunnerGitTests()
    {
        _model.RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            _events.Add("model");
            var proposal = TestProposals.New();
            foreach (Match m in LineId().Matches(call.Arg<ModelRunRequest>().Prompt))
            {
                proposal.Disposition(m.Groups[1].Value, "dropped", dropReason: "transient");
            }

            return TestModelResults.Succeeded(proposal.Build());
        });
        _git.Hook = spec =>
        {
            _events.Add(RecordingProcessRunner.SubCommand(spec));
            return null;
        };
    }

    public void Dispose() => _tree.Dispose();

    private DreamRunner Runner()
    {
        var environment = new DreamEnvironment(_tree.Root, MemoryTree.Alice, TimeZoneInfo.Utc, Path.Combine(_tree.Root, "state"),
            Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt"), null, "0.0.0-test");
        var prompts = new DreamPrompts();
        return new DreamRunner(environment, new DreamOptions(), new DreamFiler(_model, prompts, _clock), new Compressor(_model, prompts),
            new GitClient(_git, new GitClientOptions(), _clock), _clock, NullLogger<DreamRunner>.Instance);
    }

    private Task<DreamRunRecord> Run() => Runner().RunAsync(DreamTrigger.Manual, TestContext.Current.CancellationToken);

    private string[] CommitPaths() => _git.CallsOf("commit").Single().Arguments.SkipWhile(a => a != "--").Skip(1).ToArray();

    [Fact]
    public async Task Run_DetachedHead_FailedGitErrorNothingTouched()
    {
        // Arrange
        _git.On("symbolic-ref", RecordingProcessRunner.Fail(1, ""));

        // Act
        var record = await Run();

        // Assert
        record.Outcome.Should().Be("failed");
        record.Reason.Should().Be("git_error");
        record.Detail.Should().Be("not_on_branch");
        _events.Should().NotContain("model").And.NotContain("commit");
        File.Exists(_tree.Paths.Ledger).Should().BeFalse();
    }

    [Fact]
    public async Task Run_RebaseInProgress_FailedGitError()
    {
        // Arrange
        Directory.CreateDirectory(Path.Combine(_tree.Root, ".git", "rebase-merge"));
        _git.Hook = spec =>
        {
            _events.Add(RecordingProcessRunner.SubCommand(spec));
            return spec.Arguments.Contains("--git-path")
                ? RecordingProcessRunner.Ok(".git/rebase-merge\n.git/rebase-apply\n.git/MERGE_HEAD\n")
                : null;
        };

        // Act
        var record = await Run();

        // Assert
        record.Outcome.Should().Be("failed");
        record.Reason.Should().Be("git_error");
        record.Detail.Should().Be("operation_in_progress");
        _events.Should().NotContain("model");
    }

    [Fact]
    public async Task Run_PushRejectedRebaseClean_PushedSecondTime()
    {
        // Arrange
        _git.On("push", RecordingProcessRunner.Fail(1, Rejected), RecordingProcessRunner.Ok());

        // Act
        var record = await Run();

        // Assert
        record.Outcome.Should().Be("committed");
        record.Pushed.Should().BeTrue();
        _events.SkipWhile(e => e != "commit").Where(e => e is "push" or "fetch" or "rebase").Should().Equal("push", "fetch", "rebase", "push");
        _git.CallsOf("rebase").Single().Arguments.Should().Equal("-c", "rebase.autoStash=true", "rebase", "origin/main");
    }

    [Fact]
    public async Task Run_PushRejectedRebaseConflict_AbortsRebaseKeepsCommitPushedFalse()
    {
        // Arrange
        _git.On("push", RecordingProcessRunner.Fail(1, Rejected));
        _git.Hook = spec =>
        {
            _events.Add(RecordingProcessRunner.SubCommand(spec));
            return RecordingProcessRunner.SubCommand(spec) == "rebase" && !spec.Arguments.Contains("--abort")
                ? RecordingProcessRunner.Fail(1, "CONFLICT (content): Merge conflict in acme/alice/.dream/ledger.json")
                : null;
        };

        // Act
        var record = await Run();

        // Assert
        record.Outcome.Should().Be("committed");
        record.Pushed.Should().BeFalse();
        record.Commit.Should().NotBeNull();
        _git.CallsOf("rebase").Select(c => c.Arguments[^1]).Should().Equal("origin/main", "--abort");
        _git.CallsOf("push").Should().ContainSingle();
    }

    [Fact]
    public async Task Run_UnpushedDreamCommitAtStart_PushesBeforeAnythingElse()
    {
        // Arrange
        _git.On("rev-list", RecordingProcessRunner.Ok("commit 1111\ndream 2026-10-03\n"));

        // Act
        var record = await Run();

        // Assert
        record.Outcome.Should().Be("committed");
        _events.IndexOf("push").Should().BeLessThan(_events.IndexOf("model"));
        _git.CallsOf("push").Should().HaveCount(2);
    }

    [Fact]
    public async Task Run_UnpushedArchiveCommitAtStart_PushesBeforeAnythingElse()
    {
        // Arrange
        _git.On("rev-list", RecordingProcessRunner.Ok("commit 1111\narchive add zyggy/quote-2026.txt\n"));

        // Act
        var record = await Run();

        // Assert
        record.Outcome.Should().Be("committed");
        _events.IndexOf("push").Should().BeLessThan(_events.IndexOf("model"));
        _git.CallsOf("push").Should().HaveCount(2);
    }

    [Fact]
    public async Task Run_OtherStagedChanges_NotInCommitPaths()
    {
        // Arrange
        _git.On("status", RecordingProcessRunner.Ok("M  acme/alice/private/people/other.md\n"));

        // Act
        await Run();

        // Assert
        CommitPaths().Should().Equal("acme/alice/.dream/ledger.json");
    }

    [Fact]
    public async Task Run_PushFailsOtherwise_PushedFalse()
    {
        // Arrange
        _git.On("push", RecordingProcessRunner.Fail(128, "fatal: unable to access 'https://example/': Could not resolve host"));

        // Act
        var record = await Run();

        // Assert
        record.Outcome.Should().Be("committed");
        record.Pushed.Should().BeFalse();
        _git.CallsOf("fetch").Should().BeEmpty();
    }

    [Fact]
    public async Task Run_NoCallEverForcesAPush()
    {
        // Arrange
        _git.On("push", RecordingProcessRunner.Fail(1, Rejected), RecordingProcessRunner.Ok());

        // Act
        await Run();

        // Assert
        _git.Calls.Should().AllSatisfy(c => c.Arguments.Should().NotContain(a => a == "--force" || a == "-f" || a.StartsWith('+')));
    }

    [GeneratedRegex(@"^(L[0-9]+) ", RegexOptions.Multiline)]
    private static partial Regex LineId();
}
