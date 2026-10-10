using System.Text;
using System.Text.Json;

using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Memory;

/// <summary>Plan 37 Step 7 (AC-18 integration): <c>zyggy memory archive list</c> from the built binary over a seeded clone after real <c>add</c>s.</summary>
public sealed class ArchiveListCommandTests : IAsyncLifetime
{
    private readonly MemoryRepoFixture _repo = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _repo.InitializeAsync();
        await _repo.SeedAsync("archive", Ct);
    }

    public ValueTask DisposeAsync() => _repo.DisposeAsync();

    private async Task AddAsync(string project, string fileName, string name, string text)
    {
        var source = await _repo.WriteSourceAsync(fileName, Encoding.UTF8.GetBytes(text));
        var run = await _repo.ArchiveAsync(Ct, null, ["add", "--project", project, "--name", name, "--description", $"{name} for the record", "--file", source]);
        run.ExitCode.Should().Be(0, run.Stderr);
    }

    private Task<ZyggyRun> ListAsync(IReadOnlyDictionary<string, string?>? env = null, params string[] args) => _repo.ArchiveAsync(Ct, env, ["list", .. args]);

    [Fact]
    public async Task List_AfterTwoAdds_TwoUnindexedRowsInPathOrder()
    {
        // Arrange
        await AddAsync("zyggy", "roof.txt", "Roof quote", "Quote for the roof.\n");
        await AddAsync("zyggy", "gutter.txt", "Gutter quote", "Quote for the gutter.\n");

        // Act
        var run = await ListAsync();

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        run.Stderr.Should().BeEmpty();
        run.Stdout.Should().Be(
            "archive/zyggy/gutter-quote.txt  text/plain  22 B  unindexed  — Gutter quote for the record\n"
            + "archive/zyggy/roof-quote.txt  text/plain  20 B  unindexed  — Roof quote for the record\n");
    }

    [Fact]
    public async Task List_Json_ParsesWithExpectedKeys()
    {
        // Arrange
        await AddAsync("zyggy", "roof.txt", "Roof quote", "Quote for the roof.\n");

        // Act
        var run = await ListAsync(null, "--json");

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        using var document = JsonDocument.Parse(run.Stdout);
        var row = document.RootElement.EnumerateArray().Should().ContainSingle().Subject;
        row.EnumerateObject().Select(p => p.Name).Should().Equal(
            "item", "sidecar", "project", "slug", "media_type", "size_bytes", "name", "description", "archived", "state");
        row.GetProperty("item").GetString().Should().Be("archive/zyggy/roof-quote.txt");
        row.GetProperty("size_bytes").GetInt64().Should().Be(20);
        row.GetProperty("state").GetString().Should().Be("unindexed");
    }

    [Fact]
    public async Task List_ProjectFilter()
    {
        // Arrange
        await AddAsync("zyggy", "roof.txt", "Roof quote", "Quote for the roof.\n");
        await AddAsync("house-move", "boxes.txt", "Box list", "Twelve boxes.\n");

        // Act
        var run = await ListAsync(null, "--project", "house-move");

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        run.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().ContainSingle()
            .Which.Should().StartWith("archive/house-move/box-list.txt  ");
    }

    [Fact]
    public async Task List_Unindexed_AfterHandEditedProjectFileNamesItem_RowGone()
    {
        // Arrange: the dream path is Step 9; here the project file is edited by hand.
        await AddAsync("zyggy", "roof.txt", "Roof quote", "Quote for the roof.\n");
        await AddAsync("zyggy", "gutter.txt", "Gutter quote", "Quote for the gutter.\n");
        await File.AppendAllTextAsync(
            Path.Combine(_repo.PrincipalDir, "business", "areas", "zyggy.md"),
            "- [stated] 2026-10-10: Archived \"Roof quote\" (text/plain, 20 B) at archive/zyggy/roof-quote.txt — Roof quote for the record\n",
            Ct);

        // Act
        var unindexed = await ListAsync(null, "--unindexed");
        var all = await ListAsync();

        // Assert
        unindexed.Stdout.Should().Be("archive/zyggy/gutter-quote.txt  text/plain  22 B  unindexed  — Gutter quote for the record\n");
        all.Stdout.Should().Contain("archive/zyggy/roof-quote.txt  text/plain  20 B  indexed  — Roof quote for the record\n");
    }

    [Fact]
    public async Task List_OrphanSidecar_WhenItemDeletedByHand()
    {
        // Arrange
        await AddAsync("zyggy", "roof.txt", "Roof quote", "Quote for the roof.\n");
        File.Delete(Path.Combine(_repo.PrincipalDir, "archive", "zyggy", "roof-quote.txt"));

        // Act
        var run = await ListAsync();

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        run.Stdout.Should().Be("archive/zyggy/roof-quote.md  -  -  orphan  — Roof quote for the record\n");
    }

    [Fact]
    public async Task List_HooksOff_StillPrints()
    {
        // Arrange
        await AddAsync("zyggy", "roof.txt", "Roof quote", "Quote for the roof.\n");

        // Act
        var run = await ListAsync(new Dictionary<string, string?> { ["ZYGGY_HOOKS"] = "off" });

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        run.Stdout.Should().StartWith("archive/zyggy/roof-quote.txt  ");
    }

    [Fact]
    public async Task List_Empty_ExitZeroNoOutput()
    {
        // Act
        var run = await ListAsync();

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        run.Stdout.Should().BeEmpty();
        run.Stderr.Should().BeEmpty();
    }

    [Fact]
    public async Task List_TenantUnset_ExitThree()
    {
        // Act
        var run = await ListAsync(new Dictionary<string, string?> { ["ZYGGY_TENANT"] = null });

        // Assert
        run.ExitCode.Should().Be(3);
        run.Stdout.Should().BeEmpty();
        run.Stderr.Should().Be("archive: configuration error: ZYGGY_TENANT is not set\n");
    }
}
