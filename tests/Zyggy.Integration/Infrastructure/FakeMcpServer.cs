using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Zyggy.Integration.Infrastructure;

/// <summary>
/// A stand-in for the m365 MCP server on loopback HTTP/1.1: a POST without <c>Authorization: Bearer</c> gets 401 with
/// <c>WWW-Authenticate</c>; with one, JSON-RPC <c>initialize</c> and <c>tools/list</c> (from <see cref="Tools"/>) get 200. Records whether
/// each request carried a bearer — never its value.
/// </summary>
internal sealed class FakeMcpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public FakeMcpServer(IReadOnlyList<string> tools)
    {
        Tools = tools;
        _listener.Start();
        _loop = Task.Run(AcceptAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public IReadOnlyList<string> Tools { get; set; }

    /// <summary>Gets or sets the status of an unauthenticated request (401 by default).</summary>
    public int UnauthenticatedStatus { get; set; } = 401;

    public List<bool> BearerPresent { get; } = [];

    public List<string> Methods { get; } = [];

    private bool _disposed;

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _loop;
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // stopped
        }

        _stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
            await using var stream = client.GetStream();
            await HandleAsync(stream);
        }
    }

    private async Task HandleAsync(NetworkStream stream)
    {
        var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        var headers = new List<string>();
        string? line;
        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(_stop.Token)))
        {
            headers.Add(line);
        }

        var length = headers.Where(h => h.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            .Select(h => int.Parse(h["Content-Length:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture)).FirstOrDefault();
        var buffer = new char[length];
        var read = 0;
        while (read < length)
        {
            read += await reader.ReadAsync(buffer.AsMemory(read, length - read), _stop.Token);
        }

        var bearer = headers.Any(h => h.StartsWith("Authorization: Bearer ", StringComparison.OrdinalIgnoreCase));
        BearerPresent.Add(bearer);
        string status;
        var body = "{}";
        var extra = string.Empty;
        if (!bearer)
        {
            status = UnauthenticatedStatus == 401 ? "401 Unauthorized" : $"{UnauthenticatedStatus} Status";
            extra = "WWW-Authenticate: Bearer\r\n";
        }
        else
        {
            using var request = JsonDocument.Parse(new string(buffer));
            var method = request.RootElement.GetProperty("method").GetString()!;
            Methods.Add(method);
            var id = request.RootElement.GetProperty("id").GetRawText();
            var tools = string.Join(',', Tools.Select(t => "{\"name\":\"" + t + "\"}"));
            body = method == "tools/list"
                ? "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"tools\":[" + tools + "]}}"
                : "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"serverInfo\":{\"name\":\"fake\",\"version\":\"1\"}}}";
            status = "200 OK";
        }

        var bytes = Encoding.UTF8.GetBytes(body);
        var response = $"HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n{extra}\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), _stop.Token);
        await stream.WriteAsync(bytes, _stop.Token);
    }
}
