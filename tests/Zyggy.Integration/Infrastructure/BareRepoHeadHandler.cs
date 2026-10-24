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
    private string? _lastServed;

    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>Gets or sets a value indicating whether the handler lies: while true, the branch head it reports never changes.</summary>
    public bool Freeze { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
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
