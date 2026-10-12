using System.Net;

namespace Zyggy.Integration.Infrastructure;

/// <summary>
/// The in-process stand-in for the bus host's API over a bare repository (spec 04 AC-24): answers the §5 head request with
/// <c>200</c> and <c>ETag: "&lt;rev-parse main&gt;"</c>, or <c>304</c> when <c>If-None-Match</c> matches; <c>500</c> when the
/// repository cannot be read. Records every request. Never a network call.
/// </summary>
internal sealed class BareRepoHeadHandler(Func<CancellationToken, Task<string?>> headSha) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        string? sha = await headSha(cancellationToken).ConfigureAwait(false);
        if (sha is null)
        {
            return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent(string.Empty) };
        }

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
