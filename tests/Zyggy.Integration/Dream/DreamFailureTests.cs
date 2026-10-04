using Zyggy.Core.Dream;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Dream;

/// <summary>AC-21, AC-15 (subset), AC-8, AC-37 (integration): failures leave the repository, the ledger and the inbox untouched.</summary>
public sealed class DreamFailureTests : IAsyncLifetime
{
    private readonly MemoryRepoFixture _repo = new();
    private string _head = string.Empty;
    private Dictionary<string, string> _tree = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _repo.InitializeAsync();
        await _repo.SeedAsync("dream", Ct);
        _head = await _repo.GitAsync(_repo.BareDir, ["rev-parse", "main"], Ct);
        _tree = Snapshot();
    }

    public ValueTask DisposeAsync() => _repo.DisposeAsync();

    private Dictionary<string, string> Snapshot() =>
        Directory.EnumerateFiles(_repo.PrincipalDir, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, f => Convert.ToHexString(File.ReadAllBytes(f)));

    private async Task AssertUntouched()
    {
        (await _repo.GitAsync(_repo.BareDir, ["rev-parse", "main"], Ct)).Should().Be(_head);
        (await _repo.GitAsync(_repo.CloneDir, ["rev-parse", "HEAD"], Ct)).Should().Be(_head);
        Snapshot().Should().Equal(_tree);
        File.Exists(Path.Combine(_repo.PrincipalDir, ".dream", "ledger.json")).Should().BeFalse();
    }

    [Fact]
    public async Task Dream_FakeClaudeError_ExitSixNoCommitTreeAndLedgerUnchanged()
    {
        // Act
        var run = await _repo.DreamAsync("error", Ct, new Dictionary<string, string?> { ["ZYGGY_FAKE_CLAUDE_EXIT"] = "1" });

        // Assert
        run.ExitCode.Should().Be(6, run.Stderr + run.Stdout);
        await AssertUntouched();
        var record = DreamRunRecordStore.ReadLast(_repo.StateDir)!;
        record.Outcome.Should().Be("failed");
        record.Reason.Should().Be("claude_error");
    }

    [Fact]
    [Trait("Speed", "Slow")]
    public async Task Dream_FakeClaudeHang_ExitSixTimeoutNoCommitNoOrphan()
    {
        // Arrange: the smallest production timeout (1 minute, Assumption 8), a pin that matches the test binary, and a fake that
        // sleeps past the timeout.
        var instance = Path.Combine(_repo.RootDir, "instance");
        Directory.CreateDirectory(instance);
        await File.WriteAllTextAsync(Path.Combine(instance, "dream.json"), "{ \"callTimeoutMinutes\": 1 }", Ct);
        await File.WriteAllTextAsync(Path.Combine(instance, "zyggy.json"), ZyggyCli.MatchingPin(), Ct);
        var started = DateTime.UtcNow;

        // Act
        var run = await _repo.DreamAsync("dream-file-ok", Ct, new Dictionary<string, string?>
        {
            ["ZYGGY_FAKE_CLAUDE_DELAY_MS"] = "75000",
            ["ZYGGY_INSTANCE_DIR"] = instance,
        });

        // Assert
        run.ExitCode.Should().Be(6, run.Stderr + run.Stdout);
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(74), "the call was killed at its timeout");
        await AssertUntouched();
        var record = DreamRunRecordStore.ReadLast(_repo.StateDir)!;
        record.Reason.Should().Be("timeout");
        ProcessProbe.ProcessesWithWorkingDirectory(Path.Combine(_repo.StateDir, "runs")).Should().Be(0);
    }

    [Theory]
    [InlineData("secret_pattern")]
    [InlineData("path_refused")]
    [InlineData("stated_dropped")]
    public async Task Dream_BadProposal_ExitFiveCheckNamedNothingWritten(string check)
    {
        // Act
        var run = await _repo.DreamAsync("dream-file-bad-" + check, Ct);

        // Assert
        run.ExitCode.Should().Be(5, run.Stderr + run.Stdout);
        await AssertUntouched();
        var record = DreamRunRecordStore.ReadLast(_repo.StateDir)!;
        record.Outcome.Should().Be("aborted");
        record.Check.Should().Be(check);
    }

    [Fact]
    public async Task Dream_LockHeldByTest_ExitFourLockedRecordNothingTouched()
    {
        // Arrange
        Directory.CreateDirectory(_repo.StateDir);
        using var held = new FileStream(Path.Combine(_repo.StateDir, "dream.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        // Act
        var run = await _repo.DreamAsync("dream-file-ok", Ct);

        // Assert
        run.ExitCode.Should().Be(4, run.Stderr + run.Stdout);
        await AssertUntouched();
        var record = DreamRunRecordStore.ReadLast(_repo.StateDir)!;
        record.Outcome.Should().Be("failed");
        record.Reason.Should().Be("locked");
    }

    [Fact]
    public async Task Dream_PinMismatch_ExitThreeBeforeModelCall()
    {
        // Arrange
        var instance = Path.Combine(_repo.RootDir, "instance");
        Directory.CreateDirectory(instance);
        await File.WriteAllTextAsync(Path.Combine(instance, "zyggy.json"),
            """{ "version": "9.9.9", "sha256": { "linux-x64": "00", "win-x64": "00" } }""", Ct);
        var stdinPath = Path.Combine(_repo.RootDir, "stdin.bin");

        // Act
        var run = await _repo.DreamAsync("dream-file-ok", Ct, new Dictionary<string, string?>
        {
            ["ZYGGY_INSTANCE_DIR"] = instance,
            ["ZYGGY_FAKE_CLAUDE_STDIN_CAPTURE"] = stdinPath,
        });

        // Assert
        run.ExitCode.Should().Be(3);
        run.Stderr.Should().Contain("version_mismatch");
        File.Exists(stdinPath).Should().BeFalse("no model call was made");
        await AssertUntouched();
    }

    [Fact]
    public async Task Dream_TenantUnset_ExitThree()
    {
        // Act
        var run = await _repo.DreamAsync("dream-file-ok", Ct, new Dictionary<string, string?> { ["ZYGGY_TENANT"] = null });

        // Assert
        run.ExitCode.Should().Be(3);
        run.Stderr.Should().Contain("ZYGGY_TENANT");
        await AssertUntouched();
    }
}
