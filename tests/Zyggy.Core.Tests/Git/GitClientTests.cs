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
}
