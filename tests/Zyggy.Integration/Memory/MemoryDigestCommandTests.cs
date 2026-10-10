using System.Text;
using System.Text.RegularExpressions;

using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Memory;

/// <summary>Step 5: <c>zyggy memory digest &lt;section&gt;</c> honours 27's SessionStart hook contract.</summary>
public sealed partial class MemoryDigestCommandTests : IDisposable
{
    private readonly ScratchDirectory _scratch = new();

    public void Dispose() => _scratch.Dispose();

    private static string GoldenPath(params string[] parts) => Path.Combine([AppContext.BaseDirectory, "golden", "digest", .. parts]);

    private string CopyTree(string golden)
    {
        var root = Path.Combine(_scratch.Path, "memory");
        var source = Path.Combine(GoldenPath(golden), "acme", "alice");
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(root, "acme", "alice", Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        return root;
    }

    private Dictionary<string, string?> Env(string root) => new()
    {
        ["ZYGGY_MEMORY_ROOT"] = root,
        ["ZYGGY_TENANT"] = "acme",
        ["ZYGGY_USER"] = "alice",
        ["ZYGGY_TIMEZONE"] = "Europe/Brussels",
        ["CLAUDE_PROJECT_DIR"] = Project(),
    };

    private string Project()
    {
        var project = Path.Combine(_scratch.Path, "project");
        Directory.CreateDirectory(project);
        return project;
    }

    private static string HookJson(string cwd) =>
        $$"""{"session_id":"0b7c3d1e-4f5a-4b6c-8d7e-9f0a1b2c3d4e","cwd":"{{cwd.Replace("\\", "\\\\", StringComparison.Ordinal)}}","hook_event_name":"SessionStart"}""";

    private static string NormaliseGenerated(string text) => Generated().Replace(text, "generated=\"<t>\"");

    private Task<ZyggyRun> Digest(string section, Dictionary<string, string?> env, string? stdin) =>
        ZyggyCli.RunAsync(["memory", "digest", section], env, stdin, _scratch.Path, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Digest_IdentityOn27Fixture_EqualsGoldenExceptGeneratedAttribute()
    {
        // Arrange
        var root = CopyTree(Path.Combine("27", "memory"));
        var expected = Encoding.UTF8.GetString(File.ReadAllBytes(GoldenPath("27", "expected", "digest-identity.txt")));

        // Act
        var run = await Digest("identity", Env(root), HookJson(Project()));

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        run.Stderr.Should().BeEmpty();
        NormaliseGenerated(run.Stdout).Should().Be(NormaliseGenerated(expected));
    }

    [Fact]
    public async Task Digest_DailyOn27Fixture_EqualsGoldenExceptGeneratedAttribute()
    {
        // Arrange
        var root = CopyTree(Path.Combine("27", "memory"));
        var expected = Encoding.UTF8.GetString(File.ReadAllBytes(GoldenPath("27", "expected", "digest-daily.txt")));

        // Act
        var run = await Digest("daily", Env(root), HookJson(Project()));

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        NormaliseGenerated(run.Stdout).Should().Be(NormaliseGenerated(expected));
    }

    [Fact]
    public async Task Digest_IndexOnSidedFixture_WithinCapAndCategoryLinesBeforeFileLines()
    {
        // Arrange
        var root = CopyTree("sided");
        var expected = Encoding.UTF8.GetString(File.ReadAllBytes(GoldenPath("sided", "expected-index.txt")));

        // Act
        var run = await Digest("index", Env(root), null);

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        Encoding.UTF8.GetByteCount(run.Stdout).Should().BeLessThanOrEqualTo(6000);
        NormaliseGenerated(run.Stdout).Should().Be(NormaliseGenerated(expected));
    }

    [Fact]
    public async Task Digest_IndexWithArchiveItems_WithinCapListsProjectFileNoArchiveLine()
    {
        // Arrange: plan 37 Step 9 — an archived item, then the dream files its index line.
        var ct = TestContext.Current.CancellationToken;
        var repo = new MemoryRepoFixture();
        await repo.InitializeAsync();
        try
        {
            await repo.SeedAsync("archive", ct);
            repo.MaterialiseScenario("dream-archive-ok");
            var source = await repo.WriteSourceAsync("quote.txt", "Quote for the Zyggy roof repair.\nTotal 1 234,00 EUR, valid 30 days.\n"u8.ToArray());
            (await repo.ArchiveAsync(ct, null, ["add", "--project", "zyggy", "--name", "Quote 2026", "--description", "Roof repair quote", "--file", source]))
                .ExitCode.Should().Be(0);
            (await repo.DreamAsync("dream-archive-ok", ct)).ExitCode.Should().Be(0);

            // Act
            var run = await Digest("index", Env(repo.CloneDir), null);

            // Assert
            run.ExitCode.Should().Be(0, run.Stderr);
            Encoding.UTF8.GetByteCount(run.Stdout).Should().BeLessThanOrEqualTo(6000);
            run.Stdout.Should().Contain("- business/areas/zyggy.md — ").And.NotContain("archive/");
        }
        finally
        {
            await repo.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("ZYGGY_MEMORY_ROOT")]
    [InlineData("ZYGGY_TENANT")]
    [InlineData("ZYGGY_USER")]
    public async Task Digest_ConfigVariableUnset_ExitsThreeNamingItWithEmptyStdout(string variable)
    {
        // Arrange
        var env = Env(CopyTree(Path.Combine("27", "memory")));
        env[variable] = null;

        // Act
        var run = await Digest("identity", env, null);

        // Assert
        run.ExitCode.Should().Be(3);
        run.Stdout.Should().BeEmpty();
        run.Stderr.TrimEnd().Split('\n').Should().ContainSingle().Which.Should().Contain(variable).And.StartWith("zyggy: configuration error:");
    }

    [Fact]
    public async Task Digest_PrincipalDirectoryMissing_ExitsThree()
    {
        // Arrange
        var env = Env(CopyTree(Path.Combine("27", "memory")));
        env["ZYGGY_USER"] = "bob";

        // Act
        var run = await Digest("identity", env, null);

        // Assert
        run.ExitCode.Should().Be(3);
        run.Stdout.Should().BeEmpty();
        run.Stderr.Should().Contain("ZYGGY_TENANT/ZYGGY_USER");
    }

    [Fact]
    public async Task Digest_UnknownSection_ExitsFour()
    {
        // Arrange
        var env = Env(CopyTree(Path.Combine("27", "memory")));

        // Act
        var run = await Digest("everything", env, null);

        // Assert
        run.ExitCode.Should().Be(4);
        run.Stdout.Should().BeEmpty();
        run.Stderr.TrimEnd().Should().Be("zyggy: unknown section 'everything' (expected identity, index or daily)");
    }

    [Fact]
    public async Task Digest_HooksOff_ExitsZeroWithNoOutput()
    {
        // Arrange: even an unknown section and no configuration are silent when hooks are off.
        var env = new Dictionary<string, string?> { ["ZYGGY_HOOKS"] = "off" };

        // Act
        var run = await Digest("everything", env, null);

        // Assert
        run.ExitCode.Should().Be(0);
        run.Stdout.Should().BeEmpty();
        run.Stderr.Should().BeEmpty();
    }

    [Fact]
    public async Task Digest_HookJsonCwdUnderClaudeMd_WarnsOnLineTwoAndStderr()
    {
        // Arrange
        var root = CopyTree(Path.Combine("27", "memory"));
        var repo = Path.Combine(_scratch.Path, "repo");
        var cwd = Path.Combine(repo, "src");
        Directory.CreateDirectory(cwd);
        File.WriteAllText(Path.Combine(repo, "CLAUDE.md"), "# instructions\n");

        // Act
        var run = await Digest("identity", Env(root), HookJson(cwd));

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        var warning = $"[warning] CLAUDE.md found at {Path.Join(repo, "CLAUDE.md")}: AGENTS.md may not be loaded — see runbook";
        run.Stdout.Split('\n')[1].Should().Be(warning);
        run.Stderr.TrimEnd().Should().Be(warning);
    }

    [Fact]
    public async Task Digest_NoStdinRedirect_DoesNotBlock()
    {
        // Arrange
        var root = CopyTree(Path.Combine("27", "memory"));

        // Act: stdin is never written nor closed by the caller.
        var act = () => ZyggyCli.RunAsync(["memory", "digest", "identity"], Env(root), null, _scratch.Path,
            TestContext.Current.CancellationToken, keepStdinOpen: true);

        // Assert
        var run = await act.Should().CompleteWithinAsync(TimeSpan.FromSeconds(20));
        run.Subject.ExitCode.Should().Be(0);
    }

    [GeneratedRegex("generated=\"[^\"]*\"")]
    private static partial Regex Generated();
}
