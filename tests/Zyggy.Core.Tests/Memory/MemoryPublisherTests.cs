using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Git;
using Zyggy.Core.Memory;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Memory;

/// <summary>
/// The shared git path of the memory writers (spec 37 AC-15, AC-16): the preflight (branch, operation, unpushed <c>dream </c> or
/// <c>archive </c> commits pushed first), add + <c>commit --only</c> + push with one fetch-and-rebase retry, never a force push.
/// </summary>
public sealed class MemoryPublisherTests : IDisposable
{
    private const string Repo = "/srv/memory";

    private const string Rejected = " ! [rejected]        HEAD -> main (fetch first)\nerror: failed to push some refs";

    private static readonly string[] Paths = ["acme/alice/archive/zyggy/quote-2026.md", "acme/alice/archive/zyggy/quote-2026.txt"];

    private readonly RecordingProcessRunner _git = new();
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "zyggy-ut-publisher", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_temp))
        {
            Directory.Delete(_temp, recursive: true);
        }
    }

    [Fact]
    public async Task Preflight_DetachedHead_NotOnBranch()
    {
        // Arrange
        _git.On("symbolic-ref", RecordingProcessRunner.Fail(1, ""));

        // Act
        var preflight = await Publisher().PreflightAsync(Repo, TestContext.Current.CancellationToken);

        // Assert
        preflight.Branch.Should().BeNull();
        preflight.Detail.Should().Be("not_on_branch");
        _git.CallsOf("push").Should().BeEmpty();
    }

    [Fact]
    public async Task Preflight_RebaseInProgress_OperationInProgress()
    {
        // Arrange
        var rebase = Path.Combine(_temp, "rebase-merge");
        Directory.CreateDirectory(rebase);
        _git.Hook = spec => spec.Arguments.Contains("--git-path")
            ? RecordingProcessRunner.Ok(rebase + "\n/nowhere/rebase-apply\n/nowhere/MERGE_HEAD\n")
            : null;

        // Act
        var preflight = await Publisher().PreflightAsync(Repo, TestContext.Current.CancellationToken);

        // Assert
        preflight.Branch.Should().Be("main");
        preflight.Detail.Should().Be("operation_in_progress");
    }

    [Theory]
    [InlineData("dream 2026-10-03")]
    [InlineData("archive add zyggy/quote-2026.txt")]
    public async Task Preflight_UnpushedDreamOrArchiveCommit_PushesFirst(string subject)
    {
        // Arrange
        _git.On("rev-list", RecordingProcessRunner.Ok($"commit 1111\n{subject}\n"));

        // Act
        var preflight = await Publisher().PreflightAsync(Repo, TestContext.Current.CancellationToken);

        // Assert
        preflight.Should().Be(new PublisherPreflight("main", null, false));
        _git.CallsOf("push").Should().ContainSingle().Which.Arguments.Should().Equal("push", "origin", "HEAD:main");
    }

    [Fact]
    public async Task Preflight_UnpushedOtherSubject_DoesNotPush()
    {
        // Arrange
        _git.On("rev-list", RecordingProcessRunner.Ok("commit 1111\nseed\n"));

        // Act
        var preflight = await Publisher().PreflightAsync(Repo, TestContext.Current.CancellationToken);

        // Assert
        preflight.Should().Be(new PublisherPreflight("main", null, false));
        _git.CallsOf("push").Should().BeEmpty();
    }

    [Fact]
    public async Task Preflight_UnpushedPushStillRejected_PushPending()
    {
        // Arrange
        _git.On("rev-list", RecordingProcessRunner.Ok("commit 1111\narchive add zyggy/quote-2026.txt\n"));
        _git.On("push", RecordingProcessRunner.Fail(1, Rejected), RecordingProcessRunner.Fail(1, Rejected));

        // Act
        var preflight = await Publisher().PreflightAsync(Repo, TestContext.Current.CancellationToken);

        // Assert
        preflight.Should().Be(new PublisherPreflight("main", null, true));
        _git.CallsOf("fetch").Should().ContainSingle();
        _git.CallsOf("rebase").Should().ContainSingle();
    }

    [Fact]
    public async Task CommitAndPush_Sequence_AddThenCommitOnlyThenRevParseThenPush()
    {
        // Act
        var result = await Publisher().CommitAndPushAsync(Repo, "main", "archive add x\n", Paths, Paths, TestContext.Current.CancellationToken);

        // Assert
        result.Should().Be(new PublishResult(true, "0123456789abcdef0123456789abcdef01234567", true, null));
        _git.Calls.Select(RecordingProcessRunner.SubCommand).Should().Equal("add", "commit", "rev-parse", "push");
        _git.Calls[0].Arguments.Should().Equal("add", "--", Paths[0], Paths[1]);
        _git.Calls[1].Arguments.Should().Equal("commit", "--only", "-F", "-", "--", Paths[0], Paths[1]);
        _git.Calls[1].StandardInput.Should().Be("archive add x\n");
        _git.Calls[2].Arguments.Should().Equal("rev-parse", "HEAD");
        _git.Calls[3].Arguments.Should().Equal("push", "origin", "HEAD:main");
        _git.Calls.Should().AllSatisfy(c => c.WorkingDirectory.Should().Be(Repo));
    }

    [Fact]
    public async Task CommitAndPush_PushRejectedRebaseClean_PushedSecondTime()
    {
        // Arrange
        _git.On("push", RecordingProcessRunner.Fail(1, Rejected), RecordingProcessRunner.Ok());

        // Act
        var result = await Publisher().CommitAndPushAsync(Repo, "main", "m", Paths, Paths, TestContext.Current.CancellationToken);

        // Assert
        result.Pushed.Should().BeTrue();
        _git.Calls.Select(RecordingProcessRunner.SubCommand).Where(s => s is "push" or "fetch" or "rebase")
            .Should().Equal("push", "fetch", "rebase", "push");
        _git.CallsOf("rebase").Single().Arguments.Should().Equal("-c", "rebase.autoStash=true", "rebase", "origin/main");
    }

    [Fact]
    public async Task CommitAndPush_PushRejectedRebaseConflict_AbortsRebaseKeepsCommitPushedFalse()
    {
        // Arrange
        _git.On("push", RecordingProcessRunner.Fail(1, Rejected));
        _git.Hook = spec => RecordingProcessRunner.SubCommand(spec) == "rebase" && !spec.Arguments.Contains("--abort")
            ? RecordingProcessRunner.Fail(1, "CONFLICT (content): Merge conflict in acme/alice/archive/zyggy/quote-2026.md")
            : null;

        // Act
        var result = await Publisher().CommitAndPushAsync(Repo, "main", "m", Paths, Paths, TestContext.Current.CancellationToken);

        // Assert
        result.Committed.Should().BeTrue();
        result.Sha.Should().NotBeNull();
        result.Pushed.Should().BeFalse();
        _git.CallsOf("rebase").Select(c => c.Arguments[^1]).Should().Equal("origin/main", "--abort");
        _git.CallsOf("push").Should().ContainSingle();
    }

    [Fact]
    public async Task CommitAndPush_AddFails_AddFailedNoCommit()
    {
        // Arrange
        _git.On("add", RecordingProcessRunner.Fail(128, "fatal: pathspec did not match"));

        // Act
        var result = await Publisher().CommitAndPushAsync(Repo, "main", "m", Paths, Paths, TestContext.Current.CancellationToken);

        // Assert
        result.Should().Be(new PublishResult(false, null, false, "add_failed"));
        _git.CallsOf("commit").Should().BeEmpty();
        _git.CallsOf("push").Should().BeEmpty();
    }

    [Fact]
    public async Task CommitAndPush_CommitFails_CommitFailed()
    {
        // Arrange
        _git.On("commit", RecordingProcessRunner.Fail(1, "fatal: empty ident name"));

        // Act
        var result = await Publisher().CommitAndPushAsync(Repo, "main", "m", Paths, Paths, TestContext.Current.CancellationToken);

        // Assert
        result.Should().Be(new PublishResult(false, null, false, "commit_failed"));
        _git.CallsOf("push").Should().BeEmpty();
    }

    [Fact]
    public async Task AnyCall_NeverForce()
    {
        // Arrange
        _git.On("rev-list", RecordingProcessRunner.Ok("commit 1111\ndream 2026-10-03\n"));
        _git.On("push", RecordingProcessRunner.Fail(1, Rejected), RecordingProcessRunner.Fail(1, Rejected),
            RecordingProcessRunner.Fail(1, Rejected), RecordingProcessRunner.Ok());

        // Act
        await Publisher().PreflightAsync(Repo, TestContext.Current.CancellationToken);
        await Publisher().CommitAndPushAsync(Repo, "main", "m", Paths, Paths, TestContext.Current.CancellationToken);

        // Assert
        _git.Calls.Should().AllSatisfy(c => c.Arguments.Should().NotContain(a => a == "--force" || a == "-f" || a.StartsWith('+')));
    }

    private MemoryPublisher Publisher() => new(new GitClient(_git, new GitClientOptions(), new FakeTimeProvider()));
}
