using System.Globalization;
using System.Text.Json;

using Zyggy.Core.Memory;

namespace Zyggy.Core.M365.Graph;

/// <summary>A response: its status and body; <see cref="Accepted"/> for a 2xx or a tolerated status.</summary>
internal sealed record GraphResponse(int Status, string Body, bool Accepted);

/// <summary>A response, or the exit-6 message of a transport failure or exhausted throttling.</summary>
internal sealed record GraphHttpResult(GraphResponse? Response, string? Failure);

/// <summary>
/// The one HTTP exchange of the m365 verbs — <c>graph.sh</c>'s <c>http</c>: HTTPS only, no redirect followed, no proxy, 60 s; 429 and
/// 503 retried up to 5 times after <c>Retry-After</c> seconds, else 2^n s; a transport failure is exit 6 with its first line, withheld
/// when it matches a secret pattern.
/// </summary>
internal sealed class GraphHttp : IDisposable
{
    public const int Retries = 5;

    private readonly HttpClient _client;
    private readonly TimeProvider _clock;
    private readonly SecretPatterns _patterns;

    public GraphHttp(TimeProvider clock, SecretPatterns patterns)
        : this(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false }, clock, patterns, disposeHandler: true)
    {
    }

    internal GraphHttp(HttpMessageHandler handler, TimeProvider clock, SecretPatterns patterns, bool disposeHandler = false)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _client = new HttpClient(handler, disposeHandler) { Timeout = TimeSpan.FromSeconds(60) };
        _clock = clock;
        _patterns = patterns;
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
                retryAfter = response.Headers.RetryAfter?.Delta;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                var line = ex.Message.Split('\n')[0];
                if (_patterns.TryMatch(line, out var secret))
                {
                    line = "curl error text withheld: matches secret pattern " + secret;
                }

                return new GraphHttpResult(null, $"request failed ({(line.Length == 0 ? "curl failed without a message" : line)})");
            }

            if (status is 429 or 503)
            {
                attempt++;
                if (attempt > Retries)
                {
                    return tolerate(status)
                        ? new GraphHttpResult(new GraphResponse(status, body, true), null)
                        : new GraphHttpResult(null, Error(status, body));
                }

                var wait = retryAfter is { } seconds && seconds >= TimeSpan.Zero ? seconds : TimeSpan.FromSeconds(1 << attempt);
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, _clock, cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

            return new GraphHttpResult(new GraphResponse(status, body, status is >= 200 and <= 299 || tolerate(status)), null);
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

    public void Dispose() => _client.Dispose();

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
