using System.Text;

using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Memory;

/// <summary>
/// Plan 38 Step 4 (AC-3 configuration, integration): every archive sub-verb of the built binary reads <c>archive.json</c>'s <c>offload</c>
/// block — an invalid nested key is exit 3 naming <c>offload.&lt;key&gt;</c>; a valid block leaves 37's output unchanged.
/// </summary>
public sealed class ArchiveConfigurationOffloadCommandTests : IAsyncLifetime
{
    private readonly MemoryRepoFixture _repo = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _repo.InitializeAsync();
        await _repo.SeedAsync("archive", Ct);
    }

    public ValueTask DisposeAsync() => _repo.DisposeAsync();

    [Fact]
    public async Task List_InvalidOffloadKey_ExitThreeNamesKey()
    {
        // Arrange
        var env = await ConfigAsync("""{"offload": {"enabled": true, "max_failures": 0}}""");

        // Act
        var run = await _repo.ArchiveAsync(Ct, env, "list");

        // Assert
        run.ExitCode.Should().Be(3);
        run.Stdout.Should().BeEmpty();
        run.Stderr.Should().Be("archive: configuration error: offload.max_failures is above its ceiling or below its minimum (archive.json)\n");
    }

    [Fact]
    public async Task List_UnknownOffloadKey_ExitThreeNamesKey()
    {
        // Arrange
        var env = await ConfigAsync("""{"offload": {"target": "elsewhere"}}""");

        // Act
        var run = await _repo.ArchiveAsync(Ct, env, "list");

        // Assert
        run.ExitCode.Should().Be(3);
        run.Stderr.Should().Be("archive: configuration error: offload.target is not an archive.json key (archive.json)\n");
    }

    [Fact]
    public async Task List_ValidOffloadBlock_Unchanged37Output()
    {
        // Arrange
        var source = await _repo.WriteSourceAsync("roof.txt", Encoding.UTF8.GetBytes("Quote for the roof.\n"));
        var add = await _repo.ArchiveAsync(Ct, null, "add", "--project", "zyggy", "--name", "Roof quote", "--description", "Roof quote for the record", "--file", source);
        add.ExitCode.Should().Be(0, add.Stderr);
        var plain = await _repo.ArchiveAsync(Ct, null, "list");
        var env = await ConfigAsync("""{"offload": {"enabled": true, "root_folder": "Zyggy/archive", "max_items_per_run": 1}}""");

        // Act
        var run = await _repo.ArchiveAsync(Ct, env, "list");

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        run.Stderr.Should().BeEmpty();
        run.Stdout.Should().Be(plain.Stdout).And.Contain("archive/zyggy/roof-quote.txt  text/plain");
    }

    private async Task<Dictionary<string, string?>> ConfigAsync(string json)
    {
        var file = Path.Combine(_repo.RootDir, "archive.json");
        await File.WriteAllTextAsync(file, json, Ct);
        return new Dictionary<string, string?> { ["ZYGGY_ARCHIVE_CONFIG"] = file };
    }
}
