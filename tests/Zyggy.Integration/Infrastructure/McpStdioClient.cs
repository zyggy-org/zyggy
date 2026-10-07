using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Zyggy.Integration.Infrastructure;

/// <summary>
/// A hand-written newline-delimited JSON-RPC 2.0 client for an MCP server over stdio (plan 36 Step 8), independent of the SDK so the
/// oracle is not the library under test. It starts the given <c>zyggy</c> (the built one by default) with <c>linkedin mcp-server</c>
/// and an isolated environment, writes one line per message, and keeps every stdout line for the "only protocol lines" check.
/// </summary>
internal sealed class McpStdioClient : IAsyncDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);
    private static readonly string[] ServerArgs = ["linkedin", "mcp-server"];

    private readonly Process _process;
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
    private readonly StringBuilder _stderr = new();
    private readonly Task _readOut;
    private readonly Task _readErr;
    private int _nextId;

    private McpStdioClient(Process process)
    {
        _process = process;
        _readOut = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                StdoutLines.Add(line);
                await _lines.Writer.WriteAsync(line);
            }

            _lines.Writer.Complete();
        });
        _readErr = Task.Run(async () => _stderr.Append(await process.StandardError.ReadToEndAsync()));
    }

    public List<string> StdoutLines { get; } = [];

    public static McpStdioClient Start(IReadOnlyDictionary<string, string?> env, string workingDirectory, string? executable = null, params string[] extraArgs)
    {
        var info = new ProcessStartInfo(executable ?? ZyggyCli.ExecutablePath)
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
        foreach (var arg in ServerArgs.Concat(extraArgs))
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

        return new McpStdioClient(Process.Start(info) ?? throw new InvalidOperationException("zyggy did not start."));
    }

    /// <summary><c>initialize</c> then <c>notifications/initialized</c>; returns the <c>initialize</c> result.</summary>
    public async Task<JsonElement> InitializeAsync()
    {
        var result = await RequestAsync(
            "initialize",
            """{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"zyggy-tests","version":"1"}}""");
        await NotifyAsync("notifications/initialized");
        return result;
    }

    /// <summary>Sends one request and returns the whole response message with the matching id.</summary>
    public async Task<JsonElement> RequestAsync(string method, string? parametersJson = null)
    {
        var id = ++_nextId;
        await WriteAsync($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"{method}\"{(parametersJson is null ? string.Empty : ",\"params\":" + parametersJson)}}}");
        using var timeout = new CancellationTokenSource(Wait);
        while (true)
        {
            var line = await _lines.Reader.ReadAsync(timeout.Token);
            using var message = JsonDocument.Parse(line);
            if (message.RootElement.TryGetProperty("id", out var got) && got.ValueKind == JsonValueKind.Number && got.GetInt32() == id)
            {
                return message.RootElement.Clone();
            }
        }
    }

    public Task NotifyAsync(string method) => WriteAsync($"{{\"jsonrpc\":\"2.0\",\"method\":\"{method}\"}}");

    /// <summary>Closes stdin and waits for the exit: the code, and everything on stderr.</summary>
    public async Task<(int Exit, string Stderr)> CloseAndWaitAsync()
    {
        _process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(Wait);
        await _process.WaitForExitAsync(timeout.Token);
        await Task.WhenAll(_readOut, _readErr);
        return (_process.ExitCode, _stderr.ToString());
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }

        _process.Dispose();
    }

    private async Task WriteAsync(string line)
    {
        await _process.StandardInput.WriteAsync(line + "\n");
        await _process.StandardInput.FlushAsync();
    }
}
