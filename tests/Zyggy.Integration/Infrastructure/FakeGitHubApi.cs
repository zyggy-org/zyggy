using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Zyggy.Integration.Infrastructure;

/// <summary>
/// The loopback stand-in for the bus host's API, for out-of-process runs (<c>ZYGGY_BUS_API_BASE=http://127.0.0.1:&lt;port&gt;</c>):
/// answers the §5 head request with <c>200</c> + <c>ETag: "&lt;rev-parse main&gt;"</c>, or <c>304</c> when <c>If-None-Match</c>
/// matches; <c>500</c> when the repository cannot be read. Records every request with its headers (the <c>Authorization</c>
/// header is the one place the test PAT may appear). Never a network call beyond loopback.
/// </summary>
internal sealed class FakeGitHubApi : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<CancellationToken, Task<string?>> _headSha;
    private readonly Task _loop;
    private bool _disposed;

    public FakeGitHubApi(Func<CancellationToken, Task<string?>> headSha)
    {
        _headSha = headSha;
        _listener.Start();
        _loop = Task.Run(AcceptAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public string BaseAddress => $"http://127.0.0.1:{Port}";

    public ConcurrentQueue<FakeApiRequest> Requests { get; } = new();

    /// <summary>A loopback address nothing listens on, for the "connection refused" scenarios.</summary>
    public static string ClosedPortAddress()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return $"http://127.0.0.1:{port}";
    }

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
            TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token);
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            using (client)
            {
                await using NetworkStream stream = client.GetStream();
                await HandleAsync(stream);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // the client went away or the server stopped
        }
    }

    private async Task HandleAsync(NetworkStream stream)
    {
        var head = new List<byte>();
        var one = new byte[1];
        while (head.Count < 4 || !head[^4..].SequenceEqual("\r\n\r\n"u8.ToArray()))
        {
            if (await stream.ReadAsync(one, _stop.Token) == 0)
            {
                return;
            }

            head.Add(one[0]);
        }

        string[] lines = Encoding.ASCII.GetString([.. head]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        string[] requestLine = lines[0].Split(' ');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        Requests.Enqueue(new FakeApiRequest(requestLine[0], requestLine.Length > 1 ? requestLine[1] : string.Empty, headers));

        string? sha = await _headSha(_stop.Token);
        string response;
        if (sha is null)
        {
            response = "HTTP/1.1 500 Internal Server Error\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
        }
        else
        {
            string etag = "\"" + sha + "\"";
            response = headers.TryGetValue("If-None-Match", out string? match) && match == etag
                ? "HTTP/1.1 304 Not Modified\r\nx-ratelimit-remaining: 4999\r\nConnection: close\r\n\r\n"
                : string.Create(CultureInfo.InvariantCulture, $"HTTP/1.1 200 OK\r\nETag: {etag}\r\nx-ratelimit-remaining: 4999\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n[]");
        }

        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), _stop.Token);
        await stream.FlushAsync(_stop.Token);
    }
}

/// <summary>One request the fake API received.</summary>
internal sealed record FakeApiRequest(string Method, string Target, IReadOnlyDictionary<string, string> Headers);
