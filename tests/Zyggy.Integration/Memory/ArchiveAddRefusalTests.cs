using System.Runtime.Versioning;
using System.Text;

using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Memory;

/// <summary>
/// Plan 37 Step 5 (AC-7, AC-12 integration): every refusal of <c>zyggy memory archive add</c> from the built binary leaves the clone's tree,
/// index and inbox and the bare remote byte-for-byte unchanged, prints nothing on stdout and one line on stderr.
/// </summary>
public sealed class ArchiveAddRefusalTests : IAsyncLifetime
{
    private readonly MemoryRepoFixture _repo = new();

    public static bool IsLinux => OperatingSystem.IsLinux();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _repo.InitializeAsync();
        await _repo.SeedAsync("archive", Ct);
    }

    public ValueTask DisposeAsync() => _repo.DisposeAsync();

    private Task<ZyggyRun> AddAsync(string source, IReadOnlyDictionary<string, string?>? env = null, params string[] extra) =>
        _repo.ArchiveAsync(Ct, env, ["add", "--project", "zyggy", "--name", "Quote 2026", "--description", "Roof repair quote", "--file", source, .. extra]);

    private async Task<(ZyggyRun Run, string Before, string After)> RefusedAsync(string source, IReadOnlyDictionary<string, string?>? env = null, params string[] extra)
    {
        var before = await _repo.TreeFingerprintAsync(Ct);
        var run = await AddAsync(source, env, extra);
        return (run, before, await _repo.TreeFingerprintAsync(Ct));
    }

    private static void ShouldBeOneLine(ZyggyRun run, int exitCode, string stderr)
    {
        run.ExitCode.Should().Be(exitCode, run.Stderr + run.Stdout);
        run.Stdout.Should().BeEmpty();
        run.Stderr.Should().Be(stderr);
    }

    [Fact]
    public async Task Add_SecretLine_ExitTwoNamesPatternAndLineTreeUnchanged()
    {
        // Arrange
        const string Sample = "AKIAABCDEFGHIJKLMNOP";
        var source = await _repo.WriteSourceAsync("keys.txt", Encoding.UTF8.GetBytes($"Roof notes\nkey {Sample}\n"));

        // Act
        var (run, before, after) = await RefusedAsync(source);

        // Assert
        ShouldBeOneLine(run, 2, "archive: refused: secret_pattern aws-access-key (line 2)\n");
        run.Stderr.Should().NotContain(Sample);
        after.Should().Be(before);
    }

    [Fact]
    public async Task Add_SourceInsideMemory_InsideMemory()
    {
        // Act
        var (run, before, after) = await RefusedAsync(Path.Combine(_repo.PrincipalDir, "business", "areas", "zyggy.md"));

        // Assert
        ShouldBeOneLine(run, 2, "archive: refused: source_refused (inside_memory)\n");
        after.Should().Be(before);
    }

    [Fact]
    public async Task Add_SourceUnderHomeSsh_DeniedLocation()
    {
        // Arrange
        var key = Path.Combine(_repo.HomeDir, ".ssh", "id_test");
        Directory.CreateDirectory(Path.GetDirectoryName(key)!);
        await File.WriteAllTextAsync(key, "not a real key\n", Ct);

        // Act
        var (run, before, after) = await RefusedAsync(key);

        // Assert
        ShouldBeOneLine(run, 2, "archive: refused: source_refused (denied_location)\n");
        after.Should().Be(before);
    }

    [Fact]
    public async Task Add_SlugTaken_AfterFirstAdd()
    {
        // Arrange
        var source = await _repo.WriteSourceAsync("quote.txt", "Quote for the Zyggy roof repair.\n"u8.ToArray());
        var first = await AddAsync(source);
        first.ExitCode.Should().Be(0, first.Stderr);

        // Act
        var (run, before, after) = await RefusedAsync(source);

        // Assert
        ShouldBeOneLine(run, 2, "archive: refused: slug_taken\n");
        after.Should().Be(before);
        var subjects = (await _repo.GitAsync(_repo.BareDir, ["log", "--format=%s", "main"], Ct)).Split('\n');
        subjects.Count(s => s.StartsWith("archive add ", StringComparison.Ordinal)).Should().Be(1);
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "symbolic links: Linux only")]
    [SupportedOSPlatform("linux")]
    public async Task Add_SymlinkSource_OnLinux_SourceRefusedSymlink()
    {
        // Arrange
        var target = await _repo.WriteSourceAsync("real.txt", "Quote for the Zyggy roof repair.\n"u8.ToArray());
        var link = Path.Combine(_repo.SourcesDir, "link.txt");
        File.CreateSymbolicLink(link, target);

        // Act
        var (run, before, after) = await RefusedAsync(link);

        // Assert
        ShouldBeOneLine(run, 2, "archive: refused: source_refused (symlink)\n");
        after.Should().Be(before);
    }

    [Fact]
    public async Task Add_HooksOff_ExitTwoUnattendedNothingWritten()
    {
        // Arrange
        var source = await _repo.WriteSourceAsync("quote.txt", "Quote for the Zyggy roof repair.\n"u8.ToArray());

        // Act
        var (run, before, after) = await RefusedAsync(source, new Dictionary<string, string?> { ["ZYGGY_HOOKS"] = "off" });

        // Assert
        ShouldBeOneLine(run, 2, "archive: refused: unattended run\n");
        after.Should().Be(before);
    }

    [Fact]
    public async Task Add_TenantUnset_ExitThree()
    {
        // Arrange
        var source = await _repo.WriteSourceAsync("quote.txt", "Quote for the Zyggy roof repair.\n"u8.ToArray());

        // Act
        var (run, before, after) = await RefusedAsync(source, new Dictionary<string, string?> { ["ZYGGY_TENANT"] = null });

        // Assert
        ShouldBeOneLine(run, 3, "archive: configuration error: ZYGGY_TENANT is not set\n");
        after.Should().Be(before);
    }

    [Fact]
    public async Task Add_ArchiveJsonAboveCeiling_ExitThreeNamesKey()
    {
        // Arrange
        var source = await _repo.WriteSourceAsync("quote.txt", "Quote for the Zyggy roof repair.\n"u8.ToArray());
        var config = Path.Combine(_repo.RootDir, "archive.json");
        await File.WriteAllTextAsync(config, """{ "item_max_bytes": 999999999999 }""", Ct);

        // Act
        var (run, before, after) = await RefusedAsync(source, new Dictionary<string, string?> { ["ZYGGY_ARCHIVE_CONFIG"] = config });

        // Assert
        run.ExitCode.Should().Be(3, run.Stderr);
        run.Stdout.Should().BeEmpty();
        run.Stderr.Should().StartWith("archive: configuration error: item_max_bytes ");
        run.Stderr.Count(c => c == '\n').Should().Be(1);
        after.Should().Be(before);
    }

    [Fact]
    public async Task Add_UnknownOption_ExitFourUsageNamesVerb()
    {
        // Arrange
        var source = await _repo.WriteSourceAsync("quote.txt", "Quote for the Zyggy roof repair.\n"u8.ToArray());

        // Act
        var (run, before, after) = await RefusedAsync(source, null, "--bogus", "x");

        // Assert
        run.ExitCode.Should().Be(4, run.Stderr);
        run.Stdout.Should().BeEmpty();
        run.Stderr.Should().StartWith("archive: unexpected argument '--bogus' (usage: zyggy memory archive add ");
        run.Stderr.Count(c => c == '\n').Should().Be(1);
        after.Should().Be(before);
    }
}
