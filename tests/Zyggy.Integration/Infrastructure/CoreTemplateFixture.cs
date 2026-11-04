using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Zyggy.Integration.Infrastructure;

/// <summary>
/// A local stand-in for the public <c>zyggy-core</c> template (plan 42 Step 4): a bare <c>core.git</c> playing the remote and a seeding clone,
/// isolated from the machine's git configuration (its own <c>GIT_CONFIG_GLOBAL</c>, no system config, identity <c>zyggy (test)</c>,
/// <c>core.autocrlf false</c>, no credential helper). <see cref="SeedAsync"/> pushes the two node skills, the secret-pattern file and a node
/// README; <see cref="PushChangeAsync"/> moves the remote ahead. Git runs through <see cref="Process"/> directly, never the production client.
/// </summary>
public sealed class CoreTemplateFixture
{
    /// <summary>The seeded files, repository-relative with forward slashes.</summary>
    public static readonly IReadOnlyList<string> SeededFiles =
    [
        "node/skills/remember/SKILL.md",
        "node/skills/archive/SKILL.md",
        ".claude/hooks/secret-patterns.txt",
        "node/README.md",
    ];

    /// <summary>Creates the fixture under <paramref name="rootDir"/> (created by <see cref="SeedAsync"/>).</summary>
    public CoreTemplateFixture(string rootDir)
    {
        RootDir = rootDir;
        BareDir = Path.Combine(rootDir, "core.git");
        SeedDir = Path.Combine(rootDir, "seed");
        GlobalConfigPath = Path.Combine(rootDir, "gitconfig");
    }

    public string RootDir { get; }

    public string BareDir { get; }

    public string SeedDir { get; }

    public string GlobalConfigPath { get; }

    /// <summary>The bare repository as a <c>file://</c> URL: the string given to <c>--core-remote</c>, which git stores as <c>remote.origin.url</c>.</summary>
    public string RemoteUrl => new Uri(BareDir).AbsoluteUri;

    /// <summary>The isolation variables for every git call of the run under test (no global or system configuration, so no credential helper).</summary>
    public IReadOnlyDictionary<string, string?> GitEnvironment => new Dictionary<string, string?>
    {
        ["GIT_CONFIG_GLOBAL"] = GlobalConfigPath,
        ["GIT_CONFIG_NOSYSTEM"] = "1",
    };

    /// <summary>Creates the bare repository and pushes the seeded template on <c>main</c>.</summary>
    public async Task SeedAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(RootDir);
        File.Create(GlobalConfigPath).Dispose();
        await GitAsync(RootDir, ["init", "--bare", "--initial-branch=main", BareDir], ct);
        await GitAsync(RootDir, ["clone", "-q", BareDir, SeedDir], ct);
        await GitAsync(SeedDir, ["symbolic-ref", "HEAD", "refs/heads/main"], ct);
        await GitAsync(SeedDir, ["config", "user.name", "zyggy (test)"], ct);
        await GitAsync(SeedDir, ["config", "user.email", "test@zyggy.org"], ct);
        await GitAsync(SeedDir, ["config", "commit.gpgsign", "false"], ct);
        await GitAsync(SeedDir, ["config", "core.autocrlf", "false"], ct);
        Write("node/skills/remember/SKILL.md", "---\nname: remember\n---\nKeep a fact the owner stated.\nPlaceholder skill body for tests.\n");
        Write("node/skills/archive/SKILL.md", "---\nname: archive\n---\nKeep a file the owner handed over.\nPlaceholder skill body for tests.\n");
        Write(".claude/hooks/secret-patterns.txt", "synthetic-pattern-one-[0-9]{8}\nsynthetic-pattern-two-[a-f]{16}\nsynthetic-pattern-three-[A-Z]{12}\n");
        Write("node/README.md", "Node wiring for tests.\n");
        await GitAsync(SeedDir, ["add", "--all"], ct);
        await GitAsync(SeedDir, ["commit", "-q", "-m", "seed"], ct);
        await GitAsync(SeedDir, ["push", "-q", "origin", "HEAD:main"], ct);
    }

    /// <summary>Commits <paramref name="content"/> at <paramref name="relativePath"/> and pushes it; returns the new <c>main</c> SHA.</summary>
    public async Task<string> PushChangeAsync(string relativePath, string content, CancellationToken ct)
    {
        Write(relativePath, content);
        await GitAsync(SeedDir, ["add", "--", relativePath], ct);
        await GitAsync(SeedDir, ["commit", "-q", "-m", "change " + relativePath], ct);
        await GitAsync(SeedDir, ["push", "-q", "origin", "HEAD:main"], ct);
        return await HeadAsync(ct);
    }

    /// <summary>The remote's <c>main</c>.</summary>
    public Task<string> HeadAsync(CancellationToken ct) => GitAsync(BareDir, ["rev-parse", "main"], ct);

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

        foreach (var (key, value) in GitEnvironment)
        {
            startInfo.Environment[key] = value;
        }

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

    private void Write(string relativePath, string content)
    {
        string path = Path.Combine(SeedDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }
}
