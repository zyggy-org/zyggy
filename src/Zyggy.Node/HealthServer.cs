using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

using Zyggy.Core.Configuration;
using Zyggy.Core.Health;

namespace Zyggy.Node;

/// <summary>
/// <c>GET /health</c> on loopback (founding spec §11; spec 07 ex-Q1): a nested, empty web application — no configuration sources
/// (so no environment variable can rebind it), Kestrel core, routing core, <c>ListenLocalhost</c> only — started by the loop after the
/// start-up check passed and never after a refusal (plan 07 A2). Any other path is <c>404</c>, any other method <c>405</c>.
/// </summary>
internal sealed partial class HealthServer : IAsyncDisposable
{
    private readonly NodeStatus _status;
    private readonly NodeConfiguration _configuration;
    private readonly IOptions<NodeHostOptions> _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<HealthServer> _log;
    private WebApplication? _app;

    public HealthServer(NodeStatus status, NodeConfiguration configuration, IOptions<NodeHostOptions> options, ILoggerFactory loggerFactory)
    {
        _status = status;
        _configuration = configuration;
        _options = options;
        _loggerFactory = loggerFactory;
        _log = loggerFactory.CreateLogger<HealthServer>();
    }

    /// <summary>Gets a value indicating whether the server is bound.</summary>
    public bool IsListening => _app is not null;

    /// <summary>Gets the port <c>node.json</c>/the options ask for (4711 by default).</summary>
    public int ConfiguredPort => _options.Value.HealthPort;

    /// <summary>Gets the bound port (<c>0</c> before <see cref="StartAsync"/>); with <see cref="NodeHostOptions.HealthPort"/> <c>0</c> the ephemeral one.</summary>
    public int BoundPort { get; private set; }

    /// <summary>Gets the bound addresses as Kestrel reports them (loopback only).</summary>
    public IReadOnlyList<string> Addresses { get; private set; } = [];

    /// <summary>Binds and starts the server on <c>localhost:&lt;HealthPort&gt;</c>.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the server listens.</returns>
    /// <exception cref="IOException">The port is in use (the loop turns it into exit code 6).</exception>
    public async Task StartAsync(CancellationToken ct)
    {
        int port = _options.Value.HealthPort;
        WebApplicationBuilder builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        // Kestrel cannot pick an ephemeral port for "localhost" (both loopbacks); tests (port 0) bind the IPv4 loopback alone — still loopback only.
        builder.WebHost.UseKestrelCore().ConfigureKestrel(k =>
        {
            if (port == 0)
            {
                k.Listen(System.Net.IPAddress.Loopback, 0);
            }
            else
            {
                k.ListenLocalhost(port);
            }
        });
        builder.Services.AddRoutingCore();
        builder.Services.AddSingleton(_loggerFactory);
        builder.Services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        WebApplication app = builder.Build();
        app.MapGet("/health", (HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return Results.Json(Snapshot(), NodeHealthJsonContext.Default.NodeHealth);
        });
        await app.StartAsync(ct).ConfigureAwait(false);
        _app = app;
        Addresses = [.. app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses ?? []];
        BoundPort = Addresses.Select(a => new Uri(a).Port).FirstOrDefault(port);
        LogListening(BoundPort);
    }

    /// <summary>The §11 body from the loop's listener and the login probe.</summary>
    internal NodeHealth Snapshot()
    {
        Core.Bus.ListenerStatus? s = _status.Listener?.Status;
        return new NodeHealth(
            NodeHost.InformationalVersion(),
            _configuration.Tenant.Value,
            _configuration.Machine.Value,
            s?.LastPollUtc,
            s?.Poll.ETag,
            s?.LastPullUtc,
            s?.BusHeadSha,
            s?.JobsRunning ?? 0,
            s?.JobsClaimedByMe ?? 0,
            s?.LastReportUtc,
            s?.Poll.RateLimitRemaining,
            s?.Poll.BackoffUntilUtc,
            _status.ClaudeAuthOk,
            null);
    }

    /// <summary>The body as the endpoint writes it (for tests and <c>zyggy status</c>'s expectations).</summary>
    internal static string Serialize(NodeHealth health) => NodeHealthJson.Serialize(health);

    public async ValueTask DisposeAsync()
    {
        if (_app is { } app)
        {
            _app = null;
            await app.DisposeAsync().ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "health endpoint listening on http://localhost:{Port}/health")]
    private partial void LogListening(int port);
}
