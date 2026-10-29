using System.Globalization;
using System.IO.Pipelines;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

using Zyggy.Core.Hub;
using Zyggy.Core.Memory;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Hub;

/// <summary>
/// The Hub in process (spec 11 AC-21): the SDK's client over two pipes against <see cref="HubServer"/>, with a memory tree for
/// <c>acme/alice</c>, a fake clock at 2026-10-28 10:00 UTC, Brussels as +02:00 and the golden secret patterns. Disposing closes
/// the client, closes the server's stdin and checks that the server returned 0.
/// </summary>
internal sealed class HubHarness : IAsyncDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 10, 28, 10, 0, 0, TimeSpan.Zero);
    public static readonly TimeZoneInfo Brussels =
        TimeZoneInfo.CreateCustomTimeZone("Europe/Brussels", TimeSpan.FromHours(2), "Test/Brussels", "Test/Brussels");
    public static readonly SecretPatterns Patterns =
        SecretPatterns.Load(Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt")).Patterns!;

    private readonly Pipe _clientToServer = new();
    private readonly Pipe _serverToClient = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));
    private readonly StringWriter _diagnostics = new(CultureInfo.InvariantCulture) { NewLine = "\n" };
    private bool _disposed;

    private HubHarness(MemoryTree tree, bool hooksOff, DateTimeOffset now)
    {
        Tree = tree;
        Clock = new FakeTimeProvider(now);
        Start = new HubStart(null, null, tree.Paths, Brussels, Patterns, hooksOff);
    }

    public MemoryTree Tree { get; }

    public FakeTimeProvider Clock { get; }

    public HubStart Start { get; }

    public McpClient Client { get; private set; } = null!;

    public Task<int> Server { get; private set; } = null!;

    public string Diagnostics
    {
        get
        {
            lock (_diagnostics)
            {
                return _diagnostics.ToString();
            }
        }
    }

    public static async Task<HubHarness> StartAsync(MemoryTree tree, bool hooksOff = false, DateTimeOffset? now = null)
    {
        var harness = new HubHarness(tree, hooksOff, now ?? Now);
        harness.Server = HubServer.RunAsync(
            harness._clientToServer.Reader.AsStream(),
            harness._serverToClient.Writer.AsStream(),
            TextWriter.Synchronized(harness._diagnostics),
            harness.Start,
            harness.Clock,
            harness._timeout.Token);
        harness.Client = await McpClient.CreateAsync(
            new StreamClientTransport(harness._clientToServer.Writer.AsStream(), harness._serverToClient.Reader.AsStream(), NullLoggerFactory.Instance),
            clientOptions: null,
            NullLoggerFactory.Instance,
            harness._timeout.Token);
        return harness;
    }

    public static string TextOf(CallToolResult result) =>
        result.Content.Should().ContainSingle().Which.Should().BeOfType<TextContentBlock>().Which.Text;

    /// <summary>Sends the arguments exactly as written: the raw JSON object is what the server sees.</summary>
    public async Task<CallToolResult> CallAsync(string tool, string argumentsJson)
    {
        using var document = JsonDocument.Parse(argumentsJson);
        var arguments = document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        return await Client.CallToolAsync(new CallToolRequestParams { Name = tool, Arguments = arguments }, _timeout.Token);
    }

    public async Task<IList<McpClientTool>> ListToolsAsync() => await Client.ListToolsAsync(cancellationToken: _timeout.Token);

    /// <summary>Closes the client and the server's stdin, and returns the server's exit code.</summary>
    public async Task<int> StopAsync()
    {
        if (_disposed)
        {
            return await Server;
        }

        _disposed = true;
        await Client.DisposeAsync();
        await _clientToServer.Writer.CompleteAsync();
        return await Server.WaitAsync(_timeout.Token);
    }

    public async ValueTask DisposeAsync()
    {
        var exit = await StopAsync();
        _timeout.Dispose();
        await _diagnostics.DisposeAsync();
        exit.Should().Be(0);
    }
}
