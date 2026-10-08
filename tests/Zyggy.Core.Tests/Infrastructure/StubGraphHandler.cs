using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Zyggy.Core.Tests.Infrastructure;

/// <summary>
/// The login host and Graph for tests (the shell's curl stub in .NET): answers from a route table modelled on the copied
/// <c>golden/m365/graph/routes.tsv</c>, with one-shot scenario overrides consumed first. Records every request (method, URI, headers,
/// body). Records a violation — never a network call — for any host other than the two contracted ones, any <c>/me</c> path, any
/// method other than GET (and the one token POST), and any non-HTTPS URI. No test may leave a violation.
/// </summary>
internal sealed class StubGraphHandler : HttpMessageHandler
{
    private const string Login = "login.microsoftonline.com";
    private const string Graph = "graph.microsoft.com";

    private readonly List<Route> _routes = [];
    private readonly List<Route> _scenario = [];

    public List<RecordedRequest> Requests { get; } = [];

    public List<string> Violations { get; } = [];

    /// <summary>The routes of <c>golden/m365/graph/routes.tsv</c>, bodies from the same folder.</summary>
    public static StubGraphHandler FromGoldenRoutes()
    {
        var stub = new StubGraphHandler();
        foreach (var line in File.ReadAllLines(GraphFixture("routes.tsv")).Where(l => l.Length > 0 && !l.StartsWith('#')))
        {
            var parts = line.Split('\t');
            var headers = parts[4] == "-" ? null : File.ReadAllText(GraphFixture(parts[4]));
            stub._routes.Add(new Route(parts[0], new Regex(parts[1], RegexOptions.CultureInvariant), int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture), File.ReadAllText(GraphFixture(parts[3])), headers, null));
        }

        return stub;
    }

    public static string GraphFixture(string name) => Path.Combine(Golden.Directory, "m365", "graph", name);

    /// <summary>Adds a one-shot answer for the first request matching <paramref name="method"/> and <paramref name="urlPattern"/>.</summary>
    public StubGraphHandler Once(string method, string urlPattern, int status, string body = "{}", string? headers = null) =>
        Add(_scenario, new Route(method, new Regex(urlPattern, RegexOptions.CultureInvariant), status, body, headers, null));

    /// <summary>Adds a one-shot transport failure (curl's error line) for the first matching request.</summary>
    public StubGraphHandler OnceThrow(string method, string urlPattern, string message) =>
        Add(_scenario, new Route(method, new Regex(urlPattern, RegexOptions.CultureInvariant), 0, string.Empty, null, message));

    /// <summary>Adds a permanent route ahead of the golden table.</summary>
    public StubGraphHandler Always(string method, string urlPattern, int status, string body = "{}", string? headers = null)
    {
        _routes.Insert(0, new Route(method, new Regex(urlPattern, RegexOptions.CultureInvariant), status, body, headers, null));
        return this;
    }

    public IEnumerable<RecordedRequest> To(string urlPattern) => Requests.Where(r => Regex.IsMatch(r.Uri.ToString(), urlPattern, RegexOptions.CultureInvariant));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);
        Requests.Add(new RecordedRequest(request.Method, uri, headers, body));

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            Violations.Add("not https: " + uri);
        }

        var isToken = uri.Host == Login && request.Method == HttpMethod.Post && uri.AbsolutePath.EndsWith("/oauth2/v2.0/token", StringComparison.Ordinal);
        if (uri.Host is not (Login or Graph))
        {
            Violations.Add("host: " + uri.Host);
        }

        if (uri.AbsolutePath.Contains("/me/", StringComparison.Ordinal) || uri.AbsolutePath.EndsWith("/me", StringComparison.Ordinal))
        {
            Violations.Add("/me: " + uri);
        }

        if (!isToken && request.Method != HttpMethod.Get)
        {
            Violations.Add($"{request.Method}: {uri}");
        }

        var url = Uri.UnescapeDataString(uri.ToString());
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

    private StubGraphHandler Add(List<Route> list, Route route)
    {
        list.Add(route);
        return this;
    }

    private sealed record Route(string Method, Regex Url, int Status, string Body, string? Headers, string? Throw)
    {
        public bool Matches(string method, string url) => method == Method && Url.IsMatch(url);
    }
}

/// <summary>One request the stub received.</summary>
internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string? Body, byte[]? Bytes = null);
