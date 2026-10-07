namespace Zyggy.Core.LinkedIn;

/// <summary>
/// The one HTTP exchange of the LinkedIn verbs (spec 36 AC-13): HTTPS only, or the resolved loopback stand-in; no redirect followed, no
/// proxy; 30 s unless the context says otherwise; one attempt per call — never a retry.
/// </summary>
internal sealed class LinkedInHttp : IDisposable
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _client;
    private readonly Uri? _loopback;

    public LinkedInHttp(HttpMessageHandler? handler, TimeSpan? timeout, Uri? loopback)
    {
        _client = handler is null
            ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false }, disposeHandler: true)
            : new HttpClient(handler, disposeHandler: false);
        _client.Timeout = timeout ?? DefaultTimeout;
        _loopback = loopback;
    }

    /// <summary>Gets the timeout of every request.</summary>
    public TimeSpan Timeout => _client.Timeout;

    /// <summary>Sends one request, never following a redirect.</summary>
    /// <exception cref="InvalidOperationException">Thrown, before sending, for a URI that is neither HTTPS nor the loopback stand-in.</exception>
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var uri = request.RequestUri ?? throw new InvalidOperationException("A request needs an address.");
        var allowed = uri.Scheme == Uri.UriSchemeHttps
            || (_loopback is { } loopback && uri.Scheme == loopback.Scheme && uri.Host == loopback.Host && uri.Port == loopback.Port);
        return allowed
            ? _client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            : throw new InvalidOperationException("Only HTTPS requests (or the loopback test stand-in) are sent.");
    }

    public void Dispose() => _client.Dispose();
}
