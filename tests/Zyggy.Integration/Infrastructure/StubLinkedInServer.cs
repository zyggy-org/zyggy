using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Zyggy.Integration.Infrastructure;

/// <summary>
/// A stand-in for LinkedIn on loopback HTTP/1.1, modelled on <see cref="FakeMcpServer"/> (spec 36 AC-15: the binary reaches it through
/// <c>ZYGGY_LINKEDIN_API_BASE=http://127.0.0.1:&lt;port&gt;</c>). It answers <c>POST /oauth/v2/accessToken</c>, <c>GET /v2/userinfo</c> and
/// <c>POST /rest/posts</c> — and, for an image (plan 36b), <c>POST /rest/images</c> with an upload address on itself and
/// <c>PUT /dms-uploads/…</c> — from the golden successes, or from one-shot per-path answers queued by a test (status, headers, body, a delay,
/// a connection reset). It records every request; one request per connection.
/// </summary>
internal sealed class StubLinkedInServer : IAsyncDisposable
{
    public const string TokenPath = "/oauth/v2/accessToken";
    public const string UserInfoPath = "/v2/userinfo";
    public const string PostsPath = "/rest/posts";
    public const string PostUrn = "urn:li:share:7000000000000000001";
    public const string ImagesPath = "/rest/images";
    public const string UploadPrefix = "/dms-uploads/";
    public const string ImageUrn = "urn:li:image:C4E10AQFoyyAjHPMQuQ";

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<Answer>> _queued = new(StringComparer.Ordinal);
    private readonly Task _loop;
    private bool _disposed;

    public StubLinkedInServer()
    {
        _listener.Start();
        _loop = Task.Run(AcceptAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public string BaseAddress => $"http://127.0.0.1:{Port}";

    public ConcurrentQueue<StubRequest> Requests { get; } = new();

    public IEnumerable<StubRequest> To(string path) => Requests.Where(r => r.Path == path);

    /// <summary>Queues a one-shot answer for the next request to <paramref name="path"/>.</summary>
    public StubLinkedInServer Once(string path, int status, string body = "{}", string headers = "", TimeSpan? delay = null, bool reset = false)
    {
        _queued.GetOrAdd(path, _ => new ConcurrentQueue<Answer>()).Enqueue(new Answer(status, body, headers, delay, reset));
        return this;
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

    private static string Golden(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "golden", "linkedin", "http", name));

    private Answer Default(string method, string path) => (method, path) switch
    {
        ("POST", TokenPath) => new Answer(200, Golden("token-ok.json"), string.Empty, null, false),
        ("GET", UserInfoPath) => new Answer(200, Golden("userinfo-ok.json"), string.Empty, null, false),
        ("POST", PostsPath) => new Answer(201, string.Empty, $"x-restli-id: {PostUrn}\r\n", null, false),
        ("POST", ImagesPath) => new Answer(
            200,
            $$$"""{"value":{"uploadUrlExpiresAt":1650567510704,"uploadUrl":"{{{BaseAddress}}}/dms-uploads/C4E10AQFoyyAjHPMQuQ/uploaded-image/0?ut=x","image":"{{{ImageUrn}}}"}}""",
            string.Empty,
            null,
            false),
        ("PUT", _) when path.StartsWith(UploadPrefix, StringComparison.Ordinal) => new Answer(201, string.Empty, string.Empty, null, false),
        _ => new Answer(404, "{}", string.Empty, null, false),
    };

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var client = await _listener.AcceptTcpClientAsync(_stop.Token);
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            using (client)
            {
                await using var stream = client.GetStream();
                await HandleAsync(client, stream);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // the client went away (a timeout) or the server stopped
        }
    }

    private async Task HandleAsync(TcpClient client, NetworkStream stream)
    {
        // The head is ASCII up to the blank line; the body is exactly Content-Length bytes (UTF-8), read as bytes.
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

        var lines = Encoding.ASCII.GetString([.. head]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var requestLine = lines[0];
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        var length = headers.TryGetValue("Content-Length", out var value) ? int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        var buffer = new byte[length];
        var read = 0;
        while (read < length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, length - read), _stop.Token);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        var parts = requestLine.Split(' ');
        var method = parts[0];
        var target = parts.Length > 1 ? parts[1] : string.Empty;
        var path = target.Split('?')[0];
        Requests.Enqueue(new StubRequest(method, target, headers, length == 0 ? null : Encoding.UTF8.GetString(buffer, 0, read), buffer[..read]));

        var answer = _queued.TryGetValue(path, out var queue) && queue.TryDequeue(out var queued) ? queued : Default(method, path);
        if (answer.Delay is { } delay)
        {
            await Task.Delay(delay, _stop.Token);
        }

        if (answer.Reset)
        {
            client.Client.LingerState = new LingerOption(true, 0);
            client.Client.Close();
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(answer.Body);
        var response = $"HTTP/1.1 {answer.Status} Status\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n{answer.Headers}\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), _stop.Token);
        await stream.WriteAsync(bytes, _stop.Token);
    }

    private sealed record Answer(int Status, string Body, string Headers, TimeSpan? Delay, bool Reset);
}

/// <summary>One request the stand-in received: method, target (path and query), headers, body (as text and as the raw bytes).</summary>
internal sealed record StubRequest(string Method, string Target, IReadOnlyDictionary<string, string> Headers, string? Body, byte[] Bytes)
{
    public string Path => Target.Split('?')[0];
}
