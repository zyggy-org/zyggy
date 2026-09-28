using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace Zyggy.Integration.Infrastructure;

/// <summary>
/// A local bare git repository playing "origin" (GitHub) plus a working clone (the node's checkout),
/// isolated from the machine's git configuration. Launches <c>git</c> through <see cref="Process"/>
/// directly by design: test infrastructure must not depend on the production git client.
/// </summary>
public sealed class BusRepoFixture : IAsyncLifetime
{
    private readonly string _globalConfigPath;

    public BusRepoFixture()
    {
        RootDir = Path.Combine(Path.GetTempPath(), "zyggy-it", Guid.NewGuid().ToString("N"));
        BareDir = Path.Combine(RootDir, "bus.git");
        CloneDir = Path.Combine(RootDir, "bus");
        _globalConfigPath = Path.Combine(RootDir, "gitconfig");
    }

    public string RootDir { get; }

    public string BareDir { get; }

    public string CloneDir { get; }

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(RootDir);
        File.Create(_globalConfigPath).Dispose();

        var none = CancellationToken.None;
        await RunGitAsync(RootDir, ["init", "--bare", "--initial-branch=main", BareDir], none);
        await RunGitAsync(RootDir, ["clone", BareDir, CloneDir], none);
        await RunGitAsync(CloneDir, ["symbolic-ref", "HEAD", "refs/heads/main"], none);
        await RunGitAsync(CloneDir, ["config", "user.name", "zyggy (test)"], none);
        await RunGitAsync(CloneDir, ["config", "user.email", "test@zyggy.org"], none);
        await RunGitAsync(CloneDir, ["config", "commit.gpgsign", "false"], none);
        await RunGitAsync(CloneDir, ["config", "core.autocrlf", "false"], none);
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            if (Directory.Exists(RootDir))
            {
                // Git object files are read-only on Windows; Directory.Delete would throw on them.
                foreach (var file in Directory.EnumerateFiles(RootDir, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(RootDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Directories may accumulate under <temp>/zyggy-it/ — see README "Leaked temp directories".
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above.
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Runs git with the fixture's isolated environment and returns trimmed stdout.</summary>
    /// <exception cref="InvalidOperationException">git exited non-zero (stderr in the message) or could not start.</exception>
    public async Task<string> RunGitAsync(string workingDirectory, IReadOnlyList<string> args, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        startInfo.Environment["GIT_CONFIG_GLOBAL"] = _globalConfigPath;
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(
                "git >= 2.32 must be on PATH to run the integration tests (tests/Zyggy.Integration/README.md, Prerequisites).",
                ex);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"git {string.Join(' ', args)} exited with {process.ExitCode} in '{workingDirectory}':{Environment.NewLine}{stderr}"));
        }

        return stdout.Trim();
    }

    public Task<string> HeadShaAsync(CancellationToken ct) =>
        RunGitAsync(BareDir, ["rev-parse", "main"], ct);

    public Task<string> ShowAsync(string path, CancellationToken ct) =>
        RunGitAsync(BareDir, ["show", $"main:{path}"], ct);

    public Task<string> LastCommitSubjectAsync(CancellationToken ct) =>
        RunGitAsync(BareDir, ["log", "-1", "--format=%s", "main"], ct);
}
