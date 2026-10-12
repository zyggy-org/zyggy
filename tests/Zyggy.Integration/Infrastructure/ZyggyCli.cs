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

    /// <summary>Starts <c>zyggy</c> without waiting (to kill it mid-run); output is drained and discarded.</summary>
    public static Process Start(IReadOnlyList<string> args, IReadOnlyDictionary<string, string?> env, string workingDirectory)
    {
        var info = new ProcessStartInfo(ExecutablePath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
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

        var process = Process.Start(info) ?? throw new InvalidOperationException("zyggy did not start.");
        process.StandardInput.Close();
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    /// <summary>Starts <c>zyggy</c> without waiting and captures both streams line by line (for a listener the test stops later).</summary>
    public static CapturedProcess StartCapturing(IReadOnlyList<string> args, IReadOnlyDictionary<string, string?> env, string workingDirectory)
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

        var process = Process.Start(info) ?? throw new InvalidOperationException("zyggy did not start.");
        var captured = new CapturedProcess(process);
        process.OutputDataReceived += (_, e) => captured.AddStdout(e.Data);
        process.ErrorDataReceived += (_, e) => captured.AddStderr(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.StandardInput.Close();
        return captured;
    }

    /// <summary>A <c>zyggy.json</c> pin that matches the test build of <c>zyggy</c> on this runtime.</summary>
    public static string MatchingPin()
    {
        var assembly = System.Reflection.Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(ExecutablePath)!, "zyggy.dll"));
        var version = System.Reflection.CustomAttributeExtensions
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(assembly)!.InformationalVersion.Split('+')[0];
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(ExecutablePath)));
        var rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
        return $$"""{ "version": "{{version}}", "sha256": { "{{rid}}": "{{hash}}" } }""";
    }

    /// <summary>
    /// Runs <c>zyggy</c> with <paramref name="args"/> and returns its exit code and both streams. Stdin is written (when given)
    /// and closed, unless <paramref name="keepStdinOpen"/> leaves it open and unwritten for the whole run.
    /// </summary>
    public static async Task<ZyggyRun> RunAsync(
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string?> env,
        string? stdin,
        string workingDirectory,
        CancellationToken cancellationToken,
        bool keepStdinOpen = false)
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
        // A verb that refuses its arguments may exit before it reads stdin; the closed pipe is then not the test's concern, its exit and output are.
        try
        {
            if (stdin is not null)
            {
                await process.StandardInput.WriteAsync(stdin.AsMemory(), cancellationToken);
            }

            if (!keepStdinOpen)
            {
                process.StandardInput.Close();
            }
        }
        catch (IOException)
        {
        }

        await process.WaitForExitAsync(cancellationToken);
        return new ZyggyRun(process.ExitCode, await stdout, await stderr);
    }
}

/// <summary>The outcome of one <c>zyggy</c> run.</summary>
public sealed record ZyggyRun(int ExitCode, string Stdout, string Stderr);

/// <summary>A running <c>zyggy</c> whose output is captured line by line; <see cref="StopAsync"/> kills it and returns what it printed.</summary>
public sealed class CapturedProcess(Process process) : IDisposable
{
    private readonly List<string> _stdout = [];
    private readonly List<string> _stderr = [];

    public IReadOnlyList<string> Stdout
    {
        get
        {
            lock (_stdout)
            {
                return [.. _stdout];
            }
        }
    }

    public IReadOnlyList<string> Stderr
    {
        get
        {
            lock (_stderr)
            {
                return [.. _stderr];
            }
        }
    }

    public bool HasExited => process.HasExited;

    internal void AddStdout(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_stdout)
        {
            _stdout.Add(line);
        }
    }

    internal void AddStderr(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_stderr)
        {
            _stderr.Add(line);
        }
    }

    /// <summary>Kills the process tree (if still running) and waits for it to exit.</summary>
    public async Task StopAsync(CancellationToken ct)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }

        await process.WaitForExitAsync(ct);
    }

    public void Dispose() => process.Dispose();
}
