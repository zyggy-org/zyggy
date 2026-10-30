using System.IO.Pipelines;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

using Zyggy.Core.Hub;
using Zyggy.Core.Memory;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Integration.Infrastructure;

/// <summary>
/// The Hub in process over two pipes with the SDK's client (plan 13 Step 10), over a real memory tree on disk and the system clock.
/// Disposing closes the client and the server's stdin and checks that the server returned 0.
/// </summary>
internal sealed class InProcessHub : IAsyncDisposable
{
    private readonly Pipe _clientToServer = new();
    private readonly Pipe _serverToClient = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));
    private McpClient _client = null!;
    private Task<int> _server = null!;

    private InProcessHub()
    {
    }

    public static async Task<InProcessHub> StartAsync(MemoryPaths paths, TimeZoneInfo zone)
    {
        var hub = new InProcessHub();
        SecretPatterns patterns = SecretPatterns.Load(Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt")).Patterns!;
        hub._server = HubServer.RunAsync(
            hub._clientToServer.Reader.AsStream(),
            hub._serverToClient.Writer.AsStream(),
            TextWriter.Null,
            new HubStart(null, null, paths, zone, patterns, false),
            TimeProvider.System,
            hub._timeout.Token);
        hub._client = await McpClient.CreateAsync(
            new StreamClientTransport(hub._clientToServer.Writer.AsStream(), hub._serverToClient.Reader.AsStream(), NullLoggerFactory.Instance),
            clientOptions: null,
            NullLoggerFactory.Instance,
            hub._timeout.Token);
        return hub;
    }

    /// <summary>Calls <c>get_context</c> and returns the result text.</summary>
    public async Task<string> CallGetContextAsync(string topic, int? maxTokens = null)
    {
        var arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["topic"] = JsonSerializer.SerializeToElement(topic) };
        if (maxTokens is { } max)
        {
            arguments["max_tokens"] = JsonSerializer.SerializeToElement(max);
        }

        CallToolResult result = await _client.CallToolAsync(new CallToolRequestParams { Name = "get_context", Arguments = arguments }, _timeout.Token);
        result.IsError.Should().NotBe(true);
        return result.Content.Should().ContainSingle().Which.Should().BeOfType<TextContentBlock>().Which.Text;
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        await _clientToServer.Writer.CompleteAsync();
        int exit = await _server.WaitAsync(_timeout.Token);
        _timeout.Dispose();
        exit.Should().Be(0);
    }
}
