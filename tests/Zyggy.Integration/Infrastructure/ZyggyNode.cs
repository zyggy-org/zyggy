using System.Diagnostics;
using System.Text;

namespace Zyggy.Integration.Infrastructure;

/// <summary>
/// Locates the compiled <c>zyggy-node</c> service next to the test assembly and runs it as a child process with an isolated
/// environment: every <c>ZYGGY_*</c> variable of the test process is removed before the test's own are applied (spec 07 AC-22).
/// </summary>
public static class ZyggyNode
{
    /// <summary>Absolute path of the <c>zyggy-node</c> apphost in the test output.</summary>
    /// <exception cref="FileNotFoundException">The apphost is absent (the ProjectReference to Zyggy.Node is missing).</exception>
    public static string ExecutablePath
    {
        get
        {
            var path = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "zyggy-node.exe" : "zyggy-node");
            return File.Exists(path)
                ? path
                : throw new FileNotFoundException(
                    $"zyggy-node not found at '{path}'. Zyggy.Integration.csproj must reference src/Zyggy.Node/Zyggy.Node.csproj.",
                    path);
        }
    }

    /// <summary>Runs <c>zyggy-node</c> with <paramref name="args"/> to completion and returns its exit code and both streams; stdin is closed at once.</summary>
    public static async Task<ZyggyRun> RunAsync(
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string?> env,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(ExecutablePath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
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

        using var process = Process.Start(info) ?? throw new InvalidOperationException("zyggy-node did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        process.StandardInput.Close();
        await process.WaitForExitAsync(cancellationToken);
        return new ZyggyRun(process.ExitCode, await stdout, await stderr);
    }
}
