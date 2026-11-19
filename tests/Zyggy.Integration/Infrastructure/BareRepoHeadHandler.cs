using System.Net;

namespace Zyggy.Integration.Infrastructure;

/// <summary>
/// The in-process stand-in for the bus host's API over a bare repository (spec 04 AC-24): answers the §5 head request with
/// <c>200</c> and <c>ETag: "&lt;rev-parse main&gt;"</c>, or <c>304</c> when <c>If-None-Match</c> matches; <c>500</c> when the
/// repository cannot be read. Records every request. Never a network call. With <see cref="Freeze"/> set it keeps answering from
/// the SHA it served last, so later pushes stay invisible to the doorbell (plan 07 A11: the safety fetch must catch them).
/// </summary>
internal sealed class BareRepoHeadHandler(Func<CancellationToken, Task<string?>> headSha) : HttpMessageHandler
{
    private readonly Lock _probeLock = new();

    private readonly List<bool> _probed = [];

    private string? _lastServed;

    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>Gets or sets a value indicating whether the handler lies: while true, the branch head it reports never changes.</summary>
    public bool Freeze { get; set; }

    /// <summary>
    /// Evaluated at the start of every head request, before it is answered; its results are kept in request order. The node polls
    /// one request at a time and finishes a tick's pull, job pass and pickup before its next request, so what the probe saw at a
    /// request tells which poll a change was done by: a bound counted in polls, not in seconds.
    /// </summary>
    public Func<bool>? Probe { get; set; }

    /// <summary>The number of head requests seen so far.</summary>
    public int RequestCount
    {
        get
        {
            lock (_probeLock)
            {
                return _probed.Count;
            }
        }
    }

    /// <summary>What <see cref="Probe"/> returned at every request from the 0-based index <paramref name="from"/> on (false where no probe was set).</summary>
    public IReadOnlyList<bool> ProbedSince(int from)
    {
        lock (_probeLock)
        {
            return [.. _probed.Skip(from)];
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        bool probed = Probe?.Invoke() ?? false;
        lock (_probeLock)
        {
            _probed.Add(probed);
        }

        Requests.Add(request);
        string? sha = Freeze && _lastServed is not null ? _lastServed : await headSha(cancellationToken).ConfigureAwait(false);
        if (sha is null)
        {
            return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent(string.Empty) };
        }

        _lastServed = sha;
        string etag = "\"" + sha + "\"";
        if (request.Headers.TryGetValues("If-None-Match", out var values) && values.Contains(etag, StringComparer.Ordinal))
        {
            var notModified = new HttpResponseMessage(HttpStatusCode.NotModified);
            notModified.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "4999");
            return notModified;
        }

        var ok = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
        ok.Headers.TryAddWithoutValidation("ETag", etag);
        ok.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "4999");
        return ok;
    }
}
