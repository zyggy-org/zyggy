using System.Text.Json;

using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Memory;

/// <summary>
/// Plan 38 Step 3 (AC-24, AC-25 integration): the built binary lists an offloaded item (sidecar with the offload part, no item file) as
/// <c>offloaded → &lt;drive_path&gt;</c>, and removes it in one <c>archive remove</c> commit with the note that the OneDrive copy stays —
/// without any Microsoft 365 configuration.
/// </summary>
public sealed class ArchiveOffloadListRemoveCommandTests : IAsyncLifetime
{
    private const string Sidecar = "acme/alice/archive/zyggy/quote-2026.md";

    private readonly MemoryRepoFixture _repo = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _repo.InitializeAsync();
        await _repo.SeedAsync("archive-offloaded", Ct);
    }

    public ValueTask DisposeAsync() => _repo.DisposeAsync();

    [Fact]
    public async Task List_OffloadedFixture_RowOffloadedArrowDrivePath()
    {
        // Act
        var run = await _repo.ArchiveAsync(Ct, null, "list");

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        run.Stderr.Should().BeEmpty();
        run.Stdout.Should().Be("archive/zyggy/quote-2026.txt  text/plain  68 B  offloaded → Zyggy/archive/zyggy/quote-2026.txt  — Roof repair quote\n");
    }

    [Fact]
    public async Task List_Json_OffloadedRowHasDrivePath()
    {
        // Act
        var run = await _repo.ArchiveAsync(Ct, null, "list", "--json");

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        using var json = JsonDocument.Parse(run.Stdout);
        var row = json.RootElement.EnumerateArray().Should().ContainSingle().Subject;
        row.GetProperty("state").GetString().Should().Be("offloaded");
        row.GetProperty("drive_path").GetString().Should().Be("Zyggy/archive/zyggy/quote-2026.txt");
        row.TryGetProperty("offload_failures", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Remove_Offloaded_SidecarGoneOnMainOneCommitStderrNote()
    {
        // Arrange
        var commits = await _repo.CommitCountAsync(Ct);

        // Act
        var run = await _repo.ArchiveAsync(Ct, null, "remove", "zyggy/quote-2026");

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr + run.Stdout);
        (await _repo.CommitCountAsync(Ct)).Should().Be(commits + 1);
        (await _repo.LastCommitSubjectAsync(Ct)).Should().Be("archive remove zyggy/quote-2026.txt");
        (await _repo.GitAsync(_repo.BareDir, ["show", "--name-status", "--format=", "main"], Ct)).Should().Be($"D\t{Sidecar}");
        run.Stderr.Should().Be("archive: note: the OneDrive copy at Zyggy/archive/zyggy/quote-2026.txt stays; delete it in OneDrive if wanted\n");
        run.Stdout.Should().StartWith("removed: archive/zyggy/quote-2026.md\n");
    }

    [Fact]
    public async Task Remove_Offloaded_NoM365Configuration_StillExitsZero()
    {
        // Arrange: point every m365 location at nothing — remove must not need Graph or its key
        var env = new Dictionary<string, string?>
        {
            ["ZYGGY_M365_CONFIG"] = Path.Combine(_repo.RootDir, "no-such-m365.json"),
            ["ZYGGY_M365_KEY_FILE"] = Path.Combine(_repo.RootDir, "no-such.key"),
            ["ZYGGY_M365_CER_FILE"] = Path.Combine(_repo.RootDir, "no-such.cer"),
        };

        // Act
        var run = await _repo.ArchiveAsync(Ct, env, "remove", "zyggy/quote-2026");

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr + run.Stdout);
    }
}
