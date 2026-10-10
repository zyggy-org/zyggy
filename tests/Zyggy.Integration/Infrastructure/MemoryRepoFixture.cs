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
        if (Directory.Exists(ScenarioDir))
        {
            env["ZYGGY_FAKE_CLAUDE_SCENARIO_DIR"] = ScenarioDir;
        }

        foreach (var (key, value) in extra ?? new Dictionary<string, string?>())
        {
            env[key] = value;
        }

        return env;
    }

    public string ScenarioDir => Path.Combine(RootDir, "scenarios");

    /// <summary>
    /// Copies fake-claude's <c>scenarios/&lt;name&gt;.jsonl</c> into <see cref="ScenarioDir"/> with <c>{today}</c> replaced by the UTC date
    /// (plan 37 assumption A6); from then on <see cref="DreamEnv"/> points fake-claude there.
    /// </summary>
    public void MaterialiseScenario(string name)
    {
        var text = File.ReadAllText(Path.Combine(FakeClaude.ScenarioDirectory, name + ".jsonl"), Encoding.UTF8);
        Directory.CreateDirectory(ScenarioDir);
        File.WriteAllText(Path.Combine(ScenarioDir, name + ".jsonl"), Materialise(text), new UTF8Encoding(false));
    }

    public Task<ZyggyRun> DreamAsync(string scenario, CancellationToken ct, IReadOnlyDictionary<string, string?>? extra = null, params string[] args) =>
        ZyggyCli.RunAsync(["dream", .. args], DreamEnv(scenario, extra), null, RootDir, ct);

    /// <summary>
    /// The environment for a <c>zyggy memory archive</c> run: <see cref="DreamEnv"/> without the fake-claude keys, and <c>HOME</c> under
    /// <see cref="RootDir"/> so the built-in source deny-list is deterministic.
    /// </summary>
    public Dictionary<string, string?> ArchiveEnv(IReadOnlyDictionary<string, string?>? extra = null)
    {
        var env = DreamEnv("none", extra);
        env.Remove("ZYGGY_CLAUDE_PATH");
        env.Remove("ZYGGY_FAKE_CLAUDE_SCENARIO");
        env.TryAdd("HOME", HomeDir);
        env.TryAdd("USERPROFILE", HomeDir);
        return env;
    }

    public string HomeDir => Path.Combine(RootDir, "home");

    public string SourcesDir => Path.Combine(RootDir, "sources");

    public Task<ZyggyRun> ArchiveAsync(CancellationToken ct, IReadOnlyDictionary<string, string?>? extra = null, params string[] args) =>
        ZyggyCli.RunAsync(["memory", "archive", .. args], ArchiveEnv(extra), null, RootDir, ct);

    /// <summary>Writes a source file under <see cref="SourcesDir"/> and returns its absolute path.</summary>
    public async Task<string> WriteSourceAsync(string name, byte[] bytes)
    {
        var path = Path.Combine(SourcesDir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes);
        return path;
    }

    /// <summary>
    /// Every file of the clone outside <c>.git/</c> with its SHA-256, the clone's <c>status --porcelain -z</c> and <c>HEAD</c>, and the bare
    /// <c>main</c>: equal before and after a run means tree, index, inbox and remote are byte-for-byte unchanged.
    /// </summary>
    public async Task<string> TreeFingerprintAsync(CancellationToken ct)
    {
        var builder = new StringBuilder();
        var gitDir = Path.Combine(CloneDir, ".git");
        foreach (var file in Directory.EnumerateFiles(CloneDir, "*", SearchOption.AllDirectories)
                     .Where(f => !f.StartsWith(gitDir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(file, ct)));
            builder.Append(Path.GetRelativePath(CloneDir, file)).Append(' ').Append(hash).Append('\n');
        }

        builder.Append("status:").Append(await GitAsync(CloneDir, ["status", "--porcelain", "-z", "--untracked-files=all"], ct)).Append('\n');
        builder.Append("index:").Append(await GitAsync(CloneDir, ["ls-files", "-s"], ct)).Append('\n');
        builder.Append("head:").Append(await GitAsync(CloneDir, ["rev-parse", "HEAD"], ct)).Append('\n');
        builder.Append("remote:").Append(await GitAsync(BareDir, ["rev-parse", "main"], ct)).Append('\n');
        return builder.ToString();
    }

    public Task<string> ShowAsync(string path, CancellationToken ct) => GitAsync(BareDir, ["show", $"main:{path}"], ct);

    /// <summary>The raw bytes of <c>main:&lt;path&gt;</c> in the bare repository.</summary>
    public async Task<byte[]> ShowBytesAsync(string path, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo("git") { WorkingDirectory = BareDir, RedirectStandardOutput = true, UseShellExecute = false };
        startInfo.ArgumentList.Add("show");
        startInfo.ArgumentList.Add($"main:{path}");
        startInfo.Environment["GIT_CONFIG_GLOBAL"] = GlobalConfigPath;
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("git did not start.");
        using var buffer = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(buffer, ct);
        await process.WaitForExitAsync(ct);
        return process.ExitCode == 0 ? buffer.ToArray() : throw new InvalidOperationException($"git show main:{path} exited with {process.ExitCode}");
    }

    public Task<string> LastCommitSubjectAsync(CancellationToken ct) => GitAsync(BareDir, ["log", "-1", "--format=%s", "main"], ct);

    public Task<string> LastCommitBodyAsync(CancellationToken ct) => GitAsync(BareDir, ["log", "-1", "--format=%b", "main"], ct);

    public async Task<string[]> LastCommitPathsAsync(CancellationToken ct) =>
        (await GitAsync(BareDir, ["show", "--name-only", "--format=", "main"], ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    public async Task<int> CommitCountAsync(CancellationToken ct) =>
        int.Parse(await GitAsync(BareDir, ["rev-list", "--count", "main"], ct), CultureInfo.InvariantCulture);

    public Task<string> StatusAsync(CancellationToken ct) => GitAsync(CloneDir, ["status", "--porcelain=v1", "--untracked-files=all"], ct);

    /// <summary>Sets a principal-relative file's modification time to <paramref name="age"/> ago.</summary>
    public void SetMtime(string relativePath, TimeSpan age) =>
        File.SetLastWriteTimeUtc(Path.Combine(PrincipalDir, relativePath), DateTime.UtcNow - age);

    /// <summary>Commits <paramref name="content"/> to a repository-relative path from a second clone and pushes it (the remote moves ahead).</summary>
    public async Task<string> PushFromSecondCloneAsync(string path, string content, CancellationToken ct)
    {
        var second = Path.Combine(RootDir, "second-" + Guid.NewGuid().ToString("N")[..8]);
        await GitAsync(RootDir, ["clone", "-q", BareDir, second], ct);
        await GitAsync(second, ["config", "user.name", "someone else"], ct);
        await GitAsync(second, ["config", "user.email", "else@zyggy.org"], ct);
        var full = Path.Combine(second, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, content, ct);
        await GitAsync(second, ["add", "--", path], ct);
        await GitAsync(second, ["commit", "-q", "-m", "edit from elsewhere"], ct);
        await GitAsync(second, ["push", "-q", "origin", "HEAD:main"], ct);
        return await GitAsync(second, ["rev-parse", "HEAD"], ct);
    }

    /// <summary>Moves the remote's <c>main</c> to <paramref name="sha"/> (as if the conflicting push had never happened).</summary>
    public Task<string> ResetRemoteToAsync(string sha, CancellationToken ct) => GitAsync(BareDir, ["update-ref", "refs/heads/main", sha], ct);

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
