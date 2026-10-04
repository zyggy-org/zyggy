using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Zyggy.Integration.Infrastructure;

/// <summary>
/// A memory repository for the dream: a local bare <c>memory.git</c> playing GitHub, and a clone <c>memory</c> as on Central,
/// isolated from the machine's git configuration (identity <c>zyggy (test)</c>). <see cref="SeedAsync"/> copies a fixture tree,
/// commits everything except <c>inbox/</c> and pushes. Git runs through <see cref="Process"/> directly, never the production client.
/// </summary>
public sealed class MemoryRepoFixture : IAsyncLifetime
{
    public MemoryRepoFixture()
    {
        RootDir = Path.Combine(Path.GetTempPath(), "zyggy-it", Guid.NewGuid().ToString("N"));
        BareDir = Path.Combine(RootDir, "memory.git");
        CloneDir = Path.Combine(RootDir, "memory");
        StateDir = Path.Combine(RootDir, "state");
        GlobalConfigPath = Path.Combine(RootDir, "gitconfig");
    }

    public string RootDir { get; }

    public string BareDir { get; }

    public string CloneDir { get; }

    public string StateDir { get; }

    public string GlobalConfigPath { get; }

    public string PrincipalDir => Path.Combine(CloneDir, "acme", "alice");

    public static string SecretPatternsPath => Path.Combine(AppContext.BaseDirectory, "golden", "secret-patterns", "secret-patterns.txt");

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(RootDir);
        File.Create(GlobalConfigPath).Dispose();
        var none = CancellationToken.None;
        await GitAsync(RootDir, ["init", "--bare", "--initial-branch=main", BareDir], none);
        await GitAsync(RootDir, ["clone", BareDir, CloneDir], none);
        await GitAsync(CloneDir, ["symbolic-ref", "HEAD", "refs/heads/main"], none);
        await GitAsync(CloneDir, ["config", "user.name", "zyggy (test)"], none);
        await GitAsync(CloneDir, ["config", "user.email", "test@zyggy.org"], none);
        await GitAsync(CloneDir, ["config", "commit.gpgsign", "false"], none);
        await GitAsync(CloneDir, ["config", "core.autocrlf", "false"], none);
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            if (Directory.Exists(RootDir))
            {
                foreach (var file in Directory.EnumerateFiles(RootDir, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(RootDir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leaked temp directories are documented in the README.
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Copies <c>Fixtures/&lt;name&gt;/**</c> into the clone (<c>{today}</c> and <c>{d-N}</c> in file names become real UTC dates),
    /// commits everything except <c>inbox/</c> and pushes.
    /// </summary>
    public async Task SeedAsync(string fixtureName, CancellationToken ct)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureName);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Materialise(Path.GetRelativePath(source, file));
            var target = Path.Combine(CloneDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }

        await GitAsync(CloneDir, ["add", "--all", "--", ".", ":(exclude)acme/alice/inbox"], ct);
        await GitAsync(CloneDir, ["commit", "-q", "-m", "seed"], ct);
        await GitAsync(CloneDir, ["push", "-q", "origin", "HEAD:main"], ct);
    }

    public static string Materialise(string relative)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var text = relative.Replace("{today}", today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.Ordinal);
        for (var n = 1; n <= 60; n++)
        {
            text = text.Replace($"{{d-{n}}}", today.AddDays(-n).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }

        return text;
    }

    /// <summary>The environment for a <c>zyggy dream</c> run against this repository and fake-claude.</summary>
    public Dictionary<string, string?> DreamEnv(string scenario, IReadOnlyDictionary<string, string?>? extra = null)
    {
        var env = new Dictionary<string, string?>
        {
            ["ZYGGY_MEMORY_ROOT"] = CloneDir,
            ["ZYGGY_TENANT"] = "acme",
            ["ZYGGY_USER"] = "alice",
            ["ZYGGY_TIMEZONE"] = "UTC",
            ["ZYGGY_STATE_DIR"] = StateDir,
            ["ZYGGY_SECRET_PATTERNS"] = SecretPatternsPath,
            ["ZYGGY_CLAUDE_PATH"] = FakeClaude.ExecutablePath,
            ["ZYGGY_FAKE_CLAUDE_SCENARIO"] = scenario,
            ["GIT_CONFIG_GLOBAL"] = GlobalConfigPath,
        };
        foreach (var (key, value) in extra ?? new Dictionary<string, string?>())
        {
            env[key] = value;
        }

        return env;
    }

    public Task<ZyggyRun> DreamAsync(string scenario, CancellationToken ct, IReadOnlyDictionary<string, string?>? extra = null, params string[] args) =>
        ZyggyCli.RunAsync(["dream", .. args], DreamEnv(scenario, extra), null, RootDir, ct);

    public Task<string> ShowAsync(string path, CancellationToken ct) => GitAsync(BareDir, ["show", $"main:{path}"], ct);

    public Task<string> LastCommitSubjectAsync(CancellationToken ct) => GitAsync(BareDir, ["log", "-1", "--format=%s", "main"], ct);

    public Task<string> LastCommitBodyAsync(CancellationToken ct) => GitAsync(BareDir, ["log", "-1", "--format=%b", "main"], ct);

    public async Task<string[]> LastCommitPathsAsync(CancellationToken ct) =>
        (await GitAsync(BareDir, ["show", "--name-only", "--format=", "main"], ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    public async Task<int> CommitCountAsync(CancellationToken ct) =>
        int.Parse(await GitAsync(BareDir, ["rev-list", "--count", "main"], ct), CultureInfo.InvariantCulture);

    public Task<string> StatusAsync(CancellationToken ct) => GitAsync(CloneDir, ["status", "--porcelain=v1", "--untracked-files=all"], ct);

    /// <summary>Runs git with the fixture's isolated environment and returns trimmed stdout.</summary>
    /// <exception cref="InvalidOperationException">git exited non-zero.</exception>
    public async Task<string> GitAsync(string workingDirectory, IReadOnlyList<string> args, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        startInfo.Environment["GIT_CONFIG_GLOBAL"] = GlobalConfigPath;
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException("git must be on PATH to run the integration tests.", ex);
        }

        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} exited with {process.ExitCode}: {await stderr}");
        }

        return (await stdout).Trim();
    }
}
