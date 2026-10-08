using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Zyggy.Core.Tests.Infrastructure;

/// <summary>
/// LinkedIn for unit tests, modelled on <see cref="StubGraphHandler"/>: answers from a route table and one-shot scenario overrides
/// (consumed first), and records every request (method, URI, headers, body). Records a violation — never a network call — for any host
/// other than <c>api.linkedin.com</c> / <c>www.linkedin.com</c>, a non-HTTPS URI, a POST other than the token exchange and the post,
/// a method other than GET and POST, an unroutable request, or a watched secret in a URI. No test may leave a violation.
/// </summary>
internal sealed class StubLinkedInHandler : HttpMessageHandler
{
    public const string TokenUrl = "^https://www\\.linkedin\\.com/oauth/v2/accessToken$";
    public const string UserInfoUrl = "^https://api\\.linkedin\\.com/v2/userinfo$";
    public const string PostsUrl = "^https://api\\.linkedin\\.com/rest/posts$";

    private readonly List<Route> _routes = [];
    private readonly List<Route> _scenario = [];
    private readonly List<string> _watched = [];

    public List<RecordedRequest> Requests { get; } = [];

    public List<string> Violations { get; } = [];

    public static string Fixture(string name) => File.ReadAllText(Path.Combine(Golden.Directory, "linkedin", "http", name));

    /// <summary>The token exchange and the user info answering with the golden successes.</summary>
    public static StubLinkedInHandler SignInOk() =>
        new StubLinkedInHandler()
            .Always("POST", TokenUrl, 200, Fixture("token-ok.json"))
            .Always("GET", UserInfoUrl, 200, Fixture("userinfo-ok.json"));

    /// <summary>Records a violation when <paramref name="values"/> appear in a request URI.</summary>
    public StubLinkedInHandler WatchUris(params string[] values)
    {
        _watched.AddRange(values);
        return this;
    }

    public StubLinkedInHandler Always(string method, string urlPattern, int status, string body = "{}", string? headers = null)
    {
        _routes.Insert(0, new Route(method, new Regex(urlPattern, RegexOptions.CultureInvariant), status, body, headers, null, null));
        return this;
    }

    public StubLinkedInHandler Once(string method, string urlPattern, int status, string body = "{}", string? headers = null)
    {
        _scenario.Add(new Route(method, new Regex(urlPattern, RegexOptions.CultureInvariant), status, body, headers, null, null));
        return this;
    }

    /// <summary>A one-shot transport failure (a connection reset) for the first matching request.</summary>
    public StubLinkedInHandler OnceThrow(string method, string urlPattern, string message = "Connection reset by peer")
    {
        _scenario.Add(new Route(method, new Regex(urlPattern, RegexOptions.CultureInvariant), 0, string.Empty, null, message, null));
        return this;
    }

    /// <summary>A one-shot answer that waits <paramref name="delay"/> first (honouring the request's cancellation).</summary>
    public StubLinkedInHandler OnceDelayed(string method, string urlPattern, TimeSpan delay, int status = 201, string body = "{}", string? headers = null)
    {
        _scenario.Add(new Route(method, new Regex(urlPattern, RegexOptions.CultureInvariant), status, body, headers, null, delay));
        return this;
    }

    public IEnumerable<RecordedRequest> To(string urlPattern) => Requests.Where(r => Regex.IsMatch(r.Uri.ToString(), urlPattern, RegexOptions.CultureInvariant));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);
        Requests.Add(new RecordedRequest(request.Method, uri, headers, body));

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            Violations.Add("not https: " + uri);
        }

        if (uri.Host is not ("api.linkedin.com" or "www.linkedin.com"))
        {
            Violations.Add("host: " + uri.Host);
        }

        if (request.Method == HttpMethod.Post && uri.AbsolutePath is not ("/oauth/v2/accessToken" or "/rest/posts"))
        {
            Violations.Add("POST: " + uri);
        }

        if (request.Method != HttpMethod.Get && request.Method != HttpMethod.Post)
        {
            Violations.Add($"{request.Method}: {uri}");
        }

        foreach (var value in _watched.Where(v => uri.OriginalString.Contains(v, StringComparison.Ordinal)))
        {
            Violations.Add("secret in URI: " + value.Length + " characters");
        }

        var url = uri.ToString();
        var scenario = _scenario.FirstOrDefault(r => r.Matches(request.Method.Method, url));
        if (scenario is not null)
        {
            _scenario.Remove(scenario);
        }

        var route = scenario ?? _routes.FirstOrDefault(r => r.Matches(request.Method.Method, url));
        if (route is null)
        {
            Violations.Add("unroutable: " + request.Method + " " + url);
            return new HttpResponseMessage((HttpStatusCode)599) { Content = new StringContent("{}") };
        }

        if (route.Delay is { } delay)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        if (route.Throw is not null)
        {
            throw new HttpRequestException(route.Throw);
        }

        var response = new HttpResponseMessage((HttpStatusCode)route.Status) { Content = new StringContent(route.Body, Encoding.UTF8, "application/json") };
        foreach (var line in (route.Headers ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            response.Headers.TryAddWithoutValidation(line[..colon].Trim(), line[(colon + 1)..].Trim());
        }

        return response;
    }

    private sealed record Route(string Method, Regex Url, int Status, string Body, string? Headers, string? Throw, TimeSpan? Delay)
    {
        public bool Matches(string method, string url) => method == Method && Url.IsMatch(url);
    }
}
