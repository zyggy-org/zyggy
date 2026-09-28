using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Bus;

public sealed class BusRepoFixtureSmokeTests(BusRepoFixture bus) : IClassFixture<BusRepoFixture>
{
    [Fact]
    public async Task ShowAsync_AfterCommitAndPush_ReturnsContentFromBareRepository()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var content = "hello from the smoke test";
        await CommitAndPushAsync("smoke/hello.txt", content, "smoke: hello", ct);

        // Act
        var shown = await bus.ShowAsync("smoke/hello.txt", ct);

        // Assert
        shown.Should().Be(content);
    }

    [Fact]
    public async Task HeadShaAsync_AfterPush_EqualsCloneHead()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await CommitAndPushAsync("smoke/head.txt", "head", "smoke: head", ct);
        var cloneHead = await bus.RunGitAsync(bus.CloneDir, ["rev-parse", "HEAD"], ct);

        // Act
        var bareHead = await bus.HeadShaAsync(ct);

        // Assert
        bareHead.Should().Be(cloneHead);
    }

    [Fact]
    public async Task LastCommitSubjectAsync_AfterPush_ReturnsSubjectOfPushedCommit()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var subject = $"smoke: {Guid.NewGuid():N}";
        await CommitAndPushAsync("smoke/subject.txt", "subject", subject, ct);

        // Act
        var lastSubject = await bus.LastCommitSubjectAsync(ct);

        // Assert
        lastSubject.Should().Be(subject);
    }

    [Fact]
    public async Task InitializeAsync_FreshFixture_BareAndCloneAreOnMain()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;

        // Act
        var bareBranch = await bus.RunGitAsync(bus.BareDir, ["symbolic-ref", "--short", "HEAD"], ct);
        var cloneBranch = await bus.RunGitAsync(bus.CloneDir, ["symbolic-ref", "--short", "HEAD"], ct);

        // Assert
        new[] { bareBranch, cloneBranch }.Should().AllBe("main");
    }

    [Fact]
    public async Task InitializeAsync_FreshFixture_IdentityComesFromCloneLocalConfig()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;

        // Act
        var line = await bus.RunGitAsync(bus.CloneDir, ["config", "--show-origin", "--get", "user.email"], ct);

        // Assert
        var parts = line.Split('\t', 2);
        var origin = Path.GetFullPath(parts[0].Replace("file:", string.Empty, StringComparison.Ordinal), bus.CloneDir);
        origin.Should().Be(Path.Combine(bus.CloneDir, ".git", "config"));
        parts[1].Should().Be("test@zyggy.org");
    }

    [Fact]
    public async Task InitializeAsync_FreshFixture_CloneContainsOnlyGitMetadata()
    {
        // Arrange
        await using var fresh = new BusRepoFixture();

        // Act
        await fresh.InitializeAsync();

        // Assert
        Directory.EnumerateFileSystemEntries(fresh.CloneDir).Select(Path.GetFileName).Should().Equal(".git");
    }

    [Fact]
    public void Fixture_PublicSurface_HasNoTenantMember()
    {
        // Arrange
        var members = typeof(BusRepoFixture).GetMembers();

        // Act
        var tenantMembers = members.Where(m => m.Name.Contains("Tenant", StringComparison.OrdinalIgnoreCase));

        // Assert
        tenantMembers.Should().BeEmpty();
    }

    [Fact]
    public async Task RunGitAsync_NonZeroExit_ThrowsWithStderrInMessage()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var act = () => bus.RunGitAsync(bus.CloneDir, ["rev-parse", "--verify", "refs/heads/does-not-exist"], ct);

        // Act & Assert
        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Message.Should().Contain("fatal").And.Contain("refs/heads/does-not-exist");
    }

    [Fact]
    public async Task DisposeAsync_AfterInitialize_DeletesRootDir()
    {
        // Arrange
        var fixture = new BusRepoFixture();
        await fixture.InitializeAsync();

        // Act
        await fixture.DisposeAsync();

        // Assert
        Directory.Exists(fixture.RootDir).Should().BeFalse();
    }

    private async Task CommitAndPushAsync(string relativePath, string content, string subject, CancellationToken ct)
    {
        var fullPath = Path.Combine(bus.CloneDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, content, ct);
        await bus.RunGitAsync(bus.CloneDir, ["add", "--all"], ct);
        await bus.RunGitAsync(bus.CloneDir, ["commit", "-m", subject], ct);
        await bus.RunGitAsync(bus.CloneDir, ["push", "-u", "origin", "main"], ct);
    }
}
