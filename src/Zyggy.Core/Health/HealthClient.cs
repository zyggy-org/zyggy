using System.Globalization;
using System.Text.Json;

namespace Zyggy.Core.Health;

/// <summary>
/// The one HTTP client of the CLI path (spec 08 Step 5): reads <c>GET /health</c> on a <em>loopback</em> URL only — refused before any
/// request exists — through its own handler that bypasses the system proxy (a corporate proxy must never see localhost) and follows no
/// redirect, with a 2 s timeout. Every failure is a <see cref="Unreachable"/> with a constant detail; no exception message is copied (§8).
/// </summary>
public sealed class HealthClient : IDisposable
{
    /// <summary>The timeout of one read.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    private readonly HttpClient _http;
    private readonly TimeProvider _clock;

    /// <summary>Creates the client for <paramref name="healthUrl"/> with the production handler.</summary>
    /// <param name="healthUrl">The loopback health URL (<see cref="DefaultUrl"/> unless a test hook says otherwise).</param>
    /// <param name="clock">The clock (reserved for the verdict's instant; the client itself reads none).</param>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="healthUrl"/> is not a loopback address.</exception>
    public HealthClient(Uri healthUrl, TimeProvider clock)
        : this(healthUrl, clock, handler: null)
    {
    }

    /// <summary>The test constructor: <paramref name="handler"/> replaces the production one.</summary>
    internal HealthClient(Uri healthUrl, TimeProvider clock, HttpMessageHandler? handler)
    {
        ArgumentNullException.ThrowIfNull(healthUrl);
        ArgumentNullException.ThrowIfNull(clock);
        if (!healthUrl.IsLoopback)
        {
            throw new ArgumentException("health url must be loopback", nameof(healthUrl));
        }

        Url = healthUrl;
        _clock = clock;
        _http = new HttpClient(handler ?? CreateHandler(), disposeHandler: true) { Timeout = Timeout };
    }

    /// <summary>Gets the §11 default: <c>http://localhost:4711/health</c>.</summary>
    public static Uri DefaultUrl { get; } = new("http://localhost:4711/health");

    /// <summary>Gets the URL this client reads.</summary>
    public Uri Url { get; }

    /// <summary>The production handler (plan 08 A10, inspected by a test): no proxy, no redirects.</summary>
    internal static SocketsHttpHandler CreateHandler() => new() { UseProxy = false, AllowAutoRedirect = false };

    /// <summary>Reads the body once.</summary>
    /// <param name="cancellationToken">The caller's token; the only reason this method throws.</param>
    /// <returns><see cref="Reachable"/> with the body, or <see cref="Unreachable"/> with a constant detail.</returns>
    /// <exception cref="OperationCanceledException">Thrown when the caller cancels.</exception>
    public async Task<HealthProbe> ReadAsync(CancellationToken cancellationToken)
    {
        _ = _clock; // the verdict's instant is the caller's; the client reads no clock of its own
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new Unreachable("timeout");
        }
        catch (HttpRequestException)
        {
            return new Unreachable("connection refused");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return new Unreachable("HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
            }

            try
            {
                byte[] body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                NodeHealth? health = NodeHealthJson.Deserialize(body);
                return health is null ? new Unreachable("unparsable body") : new Reachable(health);
            }
            catch (JsonException)
            {
                return new Unreachable("unparsable body");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new Unreachable("timeout");
            }
            catch (HttpRequestException)
            {
                return new Unreachable("connection refused");
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _http.Dispose();
}
