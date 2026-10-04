using System.Diagnostics;
using System.Text;

namespace Zyggy.Integration.Infrastructure;

/// <summary>
/// Locates the compiled <c>zyggy</c> CLI next to the test assembly and runs it as a child process with an isolated
/// environment: every <c>ZYGGY_*</c> variable of the test process is removed before the test's own are applied.
/// </summary>
public static class ZyggyCli
{
    /// <summary>Absolute path of the <c>zyggy</c> apphost in the test output.</summary>
    /// <exception cref="FileNotFoundException">The apphost is absent (the ProjectReference to Zyggy.Cli is missing).</exception>
    public static string ExecutablePath
    {
        get
        {
            var path = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "zyggy.exe" : "zyggy");
            return File.Exists(path)
                ? path
                : throw new FileNotFoundException(
                    $"zyggy not found at '{path}'. Zyggy.Integration.csproj must reference src/Zyggy.Cli/Zyggy.Cli.csproj.",
                    path);
        }
    }

    /// <summary>Runs <c>zyggy</c> with <paramref name="args"/> and returns its exit code and both streams.</summary>
    public static async Task<ZyggyRun> RunAsync(
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string?> env,
        string? stdin,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(ExecutablePath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory,
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        foreach (var name in info.Environment.Keys.Where(k => k.StartsWith("ZYGGY_", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            info.Environment.Remove(name);
        }

        info.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var (name, value) in env)
        {
            if (value is null)
            {
                info.Environment.Remove(name);
            }
            else
            {
                info.Environment[name] = value;
            }
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException("zyggy did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin.AsMemory(), cancellationToken);
        }

        process.StandardInput.Close();
        await process.WaitForExitAsync(cancellationToken);
        return new ZyggyRun(process.ExitCode, await stdout, await stderr);
    }
}

/// <summary>The outcome of one <c>zyggy</c> run.</summary>
public sealed record ZyggyRun(int ExitCode, string Stdout, string Stderr);
