using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Git;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Git;

public sealed class GitClientTests
{
    private const string Repo = "/srv/memory";

    private readonly RecordingProcessRunner _processes = new();

    private GitClient Client() => new(_processes, new GitClientOptions(), new FakeTimeProvider());

    [Fact]
    public async Task CommitOnly_ArgumentsAreCommitOnlyFStdinDashDashPaths()
    {
        // Act
        var result = await Client().CommitOnlyAsync(Repo, "dream 2026-10-04\n\nbody\n", ["acme/alice/a.md", "acme/alice/.dream/ledger.json"],
            TestContext.Current.CancellationToken);

        // Assert
        result.Succeeded.Should().BeTrue();
        var call = _processes.Calls.Should().ContainSingle().Subject;
        call.FileName.Should().Be("git");
        call.WorkingDirectory.Should().Be(Repo);
        call.Arguments.Should().Equal("commit", "--only", "-F", "-", "--", "acme/alice/a.md", "acme/alice/.dream/ledger.json");
        call.StandardInput.Should().Be("dream 2026-10-04\n\nbody\n");
    }

    [Fact]
    public async Task Add_NewPathsOnly()
    {
        // Act
        await Client().AddAsync(Repo, ["acme/alice/business/clients/acme.md"], TestContext.Current.CancellationToken);

        // Assert
        _processes.Calls.Should().ContainSingle().Which.Arguments.Should().Equal("add", "--", "acme/alice/business/clients/acme.md");
    }

    [Fact]
    public async Task Add_NoPaths_RunsNothing()
    {
        // Act
        var result = await Client().AddAsync(Repo, [], TestContext.Current.CancellationToken);

        // Assert
        result.Succeeded.Should().BeTrue();
        _processes.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Remove_ArgumentsAreRmDashDashPaths()
    {
        // Act
        var result = await Client().RemoveAsync(Repo, ["acme/alice/archive/zyggy/quote-2026.md", "acme/alice/archive/zyggy/quote-2026.txt"],
            TestContext.Current.CancellationToken);

        // Assert
        result.Succeeded.Should().BeTrue();
        _processes.Calls.Should().ContainSingle().Which.Arguments.Should().Equal(
            "rm", "--", "acme/alice/archive/zyggy/quote-2026.md", "acme/alice/archive/zyggy/quote-2026.txt");
    }

    [Fact]
    public async Task Push_OriginHeadToBranch()
    {
        // Act
        await Client().PushAsync(Repo, "main", TestContext.Current.CancellationToken);

        // Assert
        _processes.Calls.Should().ContainSingle().Which.Arguments.Should().Equal("push", "origin", "HEAD:main");
    }

    [Fact]
    public async Task RevParse_ReturnsTrimmedSha()
    {
        // Act
        var sha = await Client().RevParseAsync(Repo, "HEAD", TestContext.Current.CancellationToken);

        // Assert
        sha.Should().Be("0123456789abcdef0123456789abcdef01234567");
    }

    [Fact]
    public async Task AnyCommand_NoShellAndPromptDisabled()
    {
        // Act
        await Client().PushAsync(Repo, "main", TestContext.Current.CancellationToken);
        await Client().StatusPorcelainAsync(Repo, TestContext.Current.CancellationToken);

        // Assert
        _processes.Calls.Should().AllSatisfy(c =>
        {
            c.FileName.Should().Be("git");
            c.Environment.Should().Contain("GIT_TERMINAL_PROMPT", "0");
            c.Timeout.Should().BePositive();
        });
    }

    [Fact]
    public async Task Failure_IsAResultNotAnException()
    {
        // Arrange
        _processes.On("push", RecordingProcessRunner.Fail(1, "! [rejected] main -> main (fetch first)"));

        // Act
        var result = await Client().PushAsync(Repo, "main", TestContext.Current.CancellationToken);

        // Assert
        result.Succeeded.Should().BeFalse();
        result.Stderr.Should().Contain("[rejected]");
    }

    [Fact]
    public async Task AnyCommand_IndexLockThenSuccess_RetriedWithTwoSecondBackoff()
    {
        // Arrange
        var clock = new FakeTimeProvider();
        var times = new List<DateTimeOffset>();
        _processes.Hook = _ =>
        {
            times.Add(clock.GetUtcNow());
            return null;
        };
        var locked = RecordingProcessRunner.Fail(128, "fatal: Unable to create '/srv/memory/.git/index.lock': File exists.");
        _processes.On("commit", locked, locked, RecordingProcessRunner.Ok());
        var client = new GitClient(_processes, new GitClientOptions(), clock);

        // Act
        var task = client.CommitOnlyAsync(Repo, "m", ["a"], TestContext.Current.CancellationToken);
        while (!task.IsCompleted)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }

        var result = await task;

        // Assert
        result.Succeeded.Should().BeTrue();
        times.Should().HaveCount(3);
        (times[1] - times[0]).Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(2));
        (times[2] - times[1]).Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task AnyCommand_IndexLockFourTimes_ReturnsGitError()
    {
        // Arrange
        var clock = new FakeTimeProvider();
        var locked = RecordingProcessRunner.Fail(128, "fatal: Unable to create '.git/index.lock': File exists.");
        _processes.On("add", locked, locked, locked, locked, RecordingProcessRunner.Ok());
        var client = new GitClient(_processes, new GitClientOptions(), clock);

        // Act
        var task = client.AddAsync(Repo, ["a"], TestContext.Current.CancellationToken);
        while (!task.IsCompleted)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }

        var result = await task;

        // Assert
        result.Succeeded.Should().BeFalse();
        _processes.CallsOf("add").Should().HaveCount(4);
    }

    [Fact]
    public async Task Rebase_UsesAutoStashOntoOriginBranch()
    {
        // Act
        await Client().FetchAsync(Repo, TestContext.Current.CancellationToken);
        await Client().RebaseAsync(Repo, "main", TestContext.Current.CancellationToken);
        await Client().RebaseAbortAsync(Repo, TestContext.Current.CancellationToken);

        // Assert
        _processes.Calls.Select(c => string.Join(' ', c.Arguments)).Should().Equal(
            "fetch origin", "-c rebase.autoStash=true rebase origin/main", "rebase --abort");
    }

    [Fact]
    public async Task UnpushedCommitSubjects_ParsesRevList()
    {
        // Arrange
        _processes.On("rev-list", RecordingProcessRunner.Ok("commit 1111\ndream 2026-10-03\ncommit 2222\nmanual edit\n"));

        // Act
        var subjects = await Client().UnpushedCommitSubjectsAsync(Repo, "main", TestContext.Current.CancellationToken);

        // Assert
        subjects.Should().Equal("dream 2026-10-03", "manual edit");
        _processes.Calls.Single().Arguments.Should().Equal("rev-list", "--format=%s", "origin/main..HEAD");
    }

    [Fact]
    public void Push_NeverForce()
    {
        // Assert: no git command line in the client source can force a push.
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Zyggy.Core", "Git", "GitClient.cs"));
        source.Should().NotContain("--force").And.NotContain("\"-f\"").And.NotContain("\"+");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Zyggy.slnx")))
        {
            dir = dir.Parent;
        }

        return dir!.FullName;
    }
}
