using Zyggy.Core.Memory;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Memory;

/// <summary>
/// Plan 38 Step 8 (AC-3, AC-4, AC-34a integration): the built binary's <c>zyggy memory archive offload</c> on every path that ends before
/// Graph — disabled, unattended and disabled, a bad <c>offload</c> key, usage, a held lock, and an enabled run without a key that fails
/// before any request; <c>add</c>/<c>remove</c> still refuse an unattended run. The binary never reaches the network.
/// </summary>
public sealed class ArchiveOffloadCommandTests : IAsyncLifetime
{
    private readonly OffloadHarness _h = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => _h.InitializeAsync();

    public ValueTask DisposeAsync() => _h.DisposeAsync();

    [Fact]
    public async Task Offload_Disabled_ExitZeroOneLine()
    {
        // Arrange: the instance's archive.json without an offload block
        _h.Configure("{}");

        // Act
        var run = await OffloadAsync();

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        run.Stdout.Should().BeEmpty();
        run.Stderr.Should().Be("offload: disabled (archive.json offload.enabled)\n");
    }

    [Fact]
    public async Task Offload_HooksOffDisabled_ExitZero()
    {
        // Arrange
        _h.Configure("{}");

        // Act
        var run = await OffloadAsync(new Dictionary<string, string?> { ["ZYGGY_HOOKS"] = "off" });

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        run.Stderr.Should().Be("offload: disabled (archive.json offload.enabled)\n");
    }

    [Fact]
    public async Task Offload_BadOffloadKey_ExitThreeNamesKey()
    {
        // Arrange
        _h.Configure("""{"offload": {"enabled": true, "root_folder": "a/b/c/d"}}""");

        // Act
        var run = await OffloadAsync();

        // Assert
        run.ExitCode.Should().Be(3);
        run.Stderr.Should().Be("offload: configuration error: offload.root_folder is above its ceiling or below its minimum (archive.json)\n");
    }

    [Fact]
    public async Task Offload_Usage_ExitFour()
    {
        // Act
        var run = await OffloadAsync(null, "--retry");

        // Assert
        run.ExitCode.Should().Be(4);
        run.Stderr.Should().Be("offload: --retry needs <project>/<slug> (usage: zyggy memory archive offload [--dry-run | --probe | --retry <project>/<slug>])\n");
    }

    [Fact]
    public async Task Offload_LockHeld_ExitZero()
    {
        // Arrange
        using var held = OffloadLock.TryAcquire(Path.Combine(_h.Repo.StateDir, "archive", "offload.lock"));

        // Act
        var run = await OffloadAsync();

        // Assert
        held.Should().NotBeNull();
        run.ExitCode.Should().Be(0, run.Stderr);
        run.Stderr.Should().Be("offload: another run is in progress\n");
    }

    [Fact]
    public async Task Offload_Enabled_NoKey_ExitSixOrThreeNoNetwork()
    {
        // Arrange: no key file, CREDENTIALS_DIRECTORY unset — the token client fails before any request
        File.Delete(_h.KeyFile);
        var before = await _h.Repo.TreeFingerprintAsync(Ct);

        // Act
        var run = await OffloadAsync();

        // Assert
        run.ExitCode.Should().BeOneOf(3, 6);
        // The key reader's own wording follows (Linux: "not found in credentials directory or file"; Windows: "not supported on this platform").
        run.Stderr.Should().StartWith("offload: sign-in failed: key: ");
        (await _h.Repo.TreeFingerprintAsync(Ct)).Should().Be(before);
    }

    [Theory]
    [InlineData("add")]
    [InlineData("remove")]
    public async Task AddRemove_HooksOff_StillRefused(string verb)
    {
        // Arrange
        string[] args = verb == "add"
            ? ["memory", "archive", "add", "--project", "zyggy", "--name", "x", "--description", "y", "--file", await _h.Repo.WriteSourceAsync("x.txt", "x\n"u8.ToArray())]
            : ["memory", "archive", "remove", "zyggy/quote-2026"];

        // Act
        var run = await ZyggyCli.RunAsync(args, _h.Env(new Dictionary<string, string?> { ["ZYGGY_HOOKS"] = "off" }), null, _h.Repo.RootDir, Ct);

        // Assert
        run.ExitCode.Should().Be(2);
        run.Stderr.Should().Be("archive: refused: unattended run\n");
    }

    private Task<ZyggyRun> OffloadAsync(IReadOnlyDictionary<string, string?>? extra = null, params string[] args) =>
        ZyggyCli.RunAsync(["memory", "archive", "offload", .. args], _h.Env(extra), null, _h.Repo.RootDir, Ct);
}
