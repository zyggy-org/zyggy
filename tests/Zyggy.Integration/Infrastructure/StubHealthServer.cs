using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Zyggy.Integration.Infrastructure;

/// <summary>
/// A loopback HTTP stand-in on an ephemeral port that answers every request with one fixed response (spec 61 slice 7): a green
/// <c>/health</c> body for <c>zyggy status</c>, or a foreign listener on the health port for <c>zyggy node stop</c>.
/// </summary>
public sealed class StubHealthServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly int _status;
    private readonly string _body;

    /// <summary>Starts answering <paramref name="body"/> with <paramref name="status"/>.</summary>
    public StubHealthServer(string body, int status = 200)
    {
        _body = body;
        _status = status;
        _listener.Start();
        Url = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/health");
        _loop = Task.Run(LoopAsync);
    }

    /// <summary>Gets the URL to set as <c>ZYGGY_HEALTH_URL</c>.</summary>
    public Uri Url { get; }

    /// <summary>Gets how many requests were answered.</summary>
    public int Requests { get; private set; }

    public async ValueTask DisposeAsync()
    {
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

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            using TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token);
            await using NetworkStream stream = client.GetStream();
            var buffer = new byte[8192];
            var request = new StringBuilder();
            while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                int read = await stream.ReadAsync(buffer, _stop.Token);
                if (read == 0)
                {
                    break;
                }

                request.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }

            byte[] body = Encoding.UTF8.GetBytes(_body);
            string head = $"HTTP/1.1 {_status} {(_status == 200 ? "OK" : "Error")}\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head), _stop.Token);
            await stream.WriteAsync(body, _stop.Token);
            Requests++;
        }
    }
}
