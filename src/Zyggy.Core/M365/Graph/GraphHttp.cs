using System.Globalization;
using System.Text.Json;

using Zyggy.Core.Memory;

namespace Zyggy.Core.M365.Graph;

/// <summary>A response: its status and body; <see cref="Accepted"/> for a 2xx or a tolerated status.</summary>
internal sealed record GraphResponse(int Status, string Body, bool Accepted);

/// <summary>
/// A response, or the exit-6 message of a transport failure or exhausted throttling. Spec 41 (plan A14): a throttled failure carries
/// the answer's <see cref="Status"/> and <see cref="RetryAfter"/>, which the pickup turns into its hold.
/// </summary>
internal sealed record GraphHttpResult(GraphResponse? Response, string? Failure)
{
    /// <summary>Gets the throttled answer's <c>Retry-After</c>, when it carried one.</summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>Gets the status of a throttled failure (429 or 503); <see langword="null"/> otherwise.</summary>
    public int? Status { get; init; }
}

/// <summary>A streamed response (spec 41 download): its status, <c>Location</c>, <c>Retry-After</c> and body, or <see cref="TooLarge"/>.</summary>
internal sealed record GraphStreamResult(int Status, Uri? Location, TimeSpan? RetryAfter, byte[] Body, bool TooLarge);

/// <summary>
/// The one HTTP exchange of the m365 verbs — <c>graph.sh</c>'s <c>http</c>: HTTPS only, no redirect followed, no proxy, 60 s; 429 and
/// 503 retried up to 5 times after <c>Retry-After</c> seconds, else 2^n s; a transport failure is exit 6 with its first line, withheld
/// when it matches a secret pattern. Central's pickup builds it with no retries (spec 41, plan A14): it never sleeps inside a poll.
/// </summary>
internal sealed class GraphHttp : IDisposable
{
    public const int Retries = 5;

    private readonly HttpClient _client;
    private readonly SecretPatterns _patterns;
    private readonly TimeProvider _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly int _retries;

    public GraphHttp(TimeProvider clock, SecretPatterns patterns)
        : this(clock, patterns, Retries)
    {
    }

    /// <summary>The production exchange with <paramref name="retries"/> throttling retries (0 for Central's pickup, plan A14).</summary>
    public GraphHttp(TimeProvider clock, SecretPatterns patterns, int retries)
        : this(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false }, clock, patterns, disposeHandler: true, retries: retries)
    {
    }

    // Tests: the stubbed handler, and a delay that records the waits instead of sleeping.
    internal GraphHttp(
        HttpMessageHandler handler,
        TimeProvider clock,
        SecretPatterns patterns,
        bool disposeHandler = false,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        int retries = Retries)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentOutOfRangeException.ThrowIfNegative(retries);
        _client = new HttpClient(handler, disposeHandler) { Timeout = TimeSpan.FromSeconds(60) };
        _patterns = patterns;
        _clock = clock;
        _retries = retries;
        _delay = delay ?? ((wait, token) => Task.Delay(wait, clock, token));
    }

    /// <summary>Sends the request <paramref name="build"/> makes (once per attempt) with the shell's retry rules.</summary>
    /// <exception cref="InvalidOperationException">Thrown for a URI that is not HTTPS.</exception>
    public async Task<GraphHttpResult> SendAsync(Func<HttpRequestMessage> build, Func<int, bool> tolerate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(tolerate);
        var attempt = 0;
        while (true)
        {
            using var request = build();
            if (request.RequestUri?.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidOperationException("Only HTTPS requests are sent.");
            }

            int status;
            string body;
            TimeSpan? retryAfter;
            try
            {
                using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                status = (int)response.StatusCode;
                body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                retryAfter = RetryAfterOf(response);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                return new GraphHttpResult(null, TransportFailure(ex));
            }

            if (status is 429 or 503)
            {
                attempt++;
                if (attempt > _retries)
                {
                    return tolerate(status)
                        ? new GraphHttpResult(new GraphResponse(status, body, true), null)
                        : new GraphHttpResult(null, _retries == Retries ? Error(status, body) : Throttled(status)) { RetryAfter = retryAfter, Status = status };
                }

                var wait = retryAfter is { } seconds && seconds >= TimeSpan.Zero ? seconds : TimeSpan.FromSeconds(1 << attempt);
                if (wait > TimeSpan.Zero)
                {
                    await _delay(wait, cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

            return new GraphHttpResult(new GraphResponse(status, body, status is >= 200 and <= 299 || tolerate(status)), null);
        }
    }

    /// <summary>
    /// Spec 41 (plan A15): sends the GET <paramref name="build"/> makes once and reads the body as a stream, stopping past
    /// <paramref name="maxBytes"/> (<see cref="GraphStreamResult.TooLarge"/>); the status, <c>Location</c> and <c>Retry-After</c> are
    /// returned as they are — no redirect followed, no retry, no sleep. A transport failure is the exit-6 message.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown for a URI that is not HTTPS.</exception>
    public async Task<(GraphStreamResult? Result, string? Failure)> SendForStreamAsync(Func<HttpRequestMessage> build, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(build);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        using var request = build();
        if (request.RequestUri?.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Only HTTPS requests are sent.");
        }

        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            var location = response.Headers.Location;
            var retryAfter = RetryAfterOf(response);
            if (status is < 200 or > 299)
            {
                var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return (new GraphStreamResult(status, location, retryAfter, System.Text.Encoding.UTF8.GetBytes(text), false), null);
            }

            if (response.Content.Headers.ContentLength is { } length && length > maxBytes)
            {
                return (new GraphStreamResult(status, location, retryAfter, [], true), null);
            }

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                using var buffer = new MemoryStream();
                var chunk = new byte[81920];
                int read;
                while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > maxBytes)
                    {
                        return (new GraphStreamResult(status, location, retryAfter, [], true), null);
                    }

                    buffer.Write(chunk, 0, read);
                }

                return (new GraphStreamResult(status, location, retryAfter, buffer.ToArray(), false), null);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException && !cancellationToken.IsCancellationRequested)
        {
            return (null, TransportFailure(ex));
        }
    }

    /// <summary><c>graph_error</c>: the exit-6 message for a failed response.</summary>
    public static string Error(int status, string body)
    {
        var code = ErrorCode(body);
        return status switch
        {
            403 => $"forbidden ({code ?? "403"}) — runbook 13 \"Scope or grant missing\"",
            429 or 503 => $"throttled ({status}) after {Retries} retries — runbook 13 \"Throttling\"",
            _ => $"Graph request failed ({status.ToString(CultureInfo.InvariantCulture)}{(code is null ? string.Empty : " " + code)})",
        };
    }

    /// <summary>The message of a throttled answer when no retry was made (Central's pickup holds instead, plan A14).</summary>
    public static string Throttled(int status) =>
        $"throttled ({status.ToString(CultureInfo.InvariantCulture)}) — runbook 13 \"Throttling\"";

    public void Dispose() => _client.Dispose();

    // The first line of a transport failure, withheld when it matches a secret pattern.
    private string TransportFailure(Exception ex)
    {
        var line = ex.Message.Split('\n')[0];
        if (_patterns.TryMatch(line, out var secret))
        {
            line = "curl error text withheld: matches secret pattern " + secret;
        }

        return $"request failed ({(line.Length == 0 ? "curl failed without a message" : line)})";
    }

    // Retry-After as seconds or as a date (measured against the injected clock).
    private TimeSpan? RetryAfterOf(HttpResponseMessage response) =>
        response.Headers.RetryAfter is not { } header ? null
        : header.Delta is { } delta ? delta
        : header.Date is { } date ? date - _clock.GetUtcNow()
        : null;

    // jq -r '.error.code // empty'
    private static string? ErrorCode(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.String
                && code.GetString() is { Length: > 0 } text
                    ? text
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
