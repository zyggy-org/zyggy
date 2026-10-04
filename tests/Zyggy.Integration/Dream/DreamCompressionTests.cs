using Zyggy.Core.Dream;
using Zyggy.Core.Memory;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Dream;

/// <summary>AC-16 (integration): a file over 300 body lines is compressed in a run with an empty inbox.</summary>
public sealed class DreamCompressionTests : IAsyncLifetime
{
    private readonly MemoryRepoFixture _repo = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _repo.InitializeAsync();
        await _repo.SeedAsync("dream", Ct);
        Directory.Delete(Path.Combine(_repo.PrincipalDir, "inbox"), recursive: true);
        var lines = Enumerable.Range(1, 310).Select(n => $"- [observed] 2026-09-20 [github-inventory 2026-09-20]: zyggy fact {n}.");
        await File.WriteAllTextAsync(Path.Combine(_repo.PrincipalDir, "business", "areas", "zyggy.md"),
            "---\nname: zyggy\ndescription: Zyggy, the agent platform\nupdated: 2026-09-28\n---\n" + string.Concat(lines.Select(l => l + "\n")), Ct);
        await _repo.GitAsync(_repo.CloneDir, ["commit", "-q", "-am", "grow zyggy.md"], Ct);
        await _repo.GitAsync(_repo.CloneDir, ["push", "-q", "origin", "HEAD:main"], Ct);
    }

    public ValueTask DisposeAsync() => _repo.DisposeAsync();

    [Fact]
    public async Task Dream_FileOver300LinesEmptyInbox_CompressedCommitWithin300()
    {
        // Act
        var run = await _repo.DreamAsync("dream-compress-ok", Ct);

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr + run.Stdout);
        var file = MemoryFileReader.Parse(await _repo.ShowAsync("acme/alice/business/areas/zyggy.md", Ct) + "\n");
        file.BodyLines.Should().HaveCount(300).And.Contain("- [observed] 2026-09-20 [github-inventory 2026-09-20]: zyggy facts 1 to 11.");
        var record = DreamRunRecordStore.ReadLast(_repo.StateDir)!;
        record.Compressions.Should().ContainSingle().Which.Result.Should().Be("accepted");
        record.Batches.Should().BeEmpty();
    }
}
