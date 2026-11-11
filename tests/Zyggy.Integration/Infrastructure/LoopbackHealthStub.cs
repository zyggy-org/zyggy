using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Zyggy.Integration.Infrastructure;

/// <summary>
/// A loopback stand-in for a node's <c>/health</c> endpoint (plan 14 Step 6): answers every request with HTTP 200 and the given JSON body
/// until disposed. Raw TCP, so no URL reservation or administrator right is needed on Windows.
/// </summary>
internal sealed class LoopbackHealthStub : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly byte[] _response;

    public LoopbackHealthStub(string jsonBody)
    {
        byte[] body = Encoding.UTF8.GetBytes(jsonBody);
        _response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nConnection: close\r\nContent-Length: "
            + body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\r\n\r\n")
            .Concat(body).ToArray();
        _listener.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/health";
        _loop = ServeAsync(_stop.Token);
    }

    public string Url { get; }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
            // stopped
        }

        _stop.Dispose();
    }

    private async Task ServeAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct);
            }
            catch (Exception exception) when (exception is ObjectDisposedException or SocketException or OperationCanceledException)
            {
                return;
            }

            using (client)
            {
                NetworkStream stream = client.GetStream();
                var buffer = new byte[4096];
                var request = new StringBuilder();
                while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    int read = await stream.ReadAsync(buffer, ct);
                    if (read == 0)
                    {
                        break;
                    }

                    request.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }

                await stream.WriteAsync(_response, ct);
                await stream.FlushAsync(ct);
            }
        }
    }
}
