using System.Net;

namespace Zyggy.Core.Tests.Infrastructure;

/// <summary>
/// An <see cref="HttpMessageHandler"/> scripted per request for the bus poller: each queued step answers with a response,
/// throws, or hangs until the request's cancellation token fires. Records every request (method, URI, headers, token).
/// Never a network call: an unscripted request is a <c>599</c> and a violation.
/// </summary>
internal sealed class StubGitHubHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _steps = new();

    public List<RecordedRequest> Requests { get; } = [];

    public List<CancellationToken> Tokens { get; } = [];

    public List<string> Violations { get; } = [];

    /// <summary>Queues a response with the given status and raw headers (<c>name: value</c> per entry).</summary>
    public StubGitHubHandler Respond(int status, params string[] headers)
    {
        _steps.Enqueue((_, _) =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("[]") };
            foreach (var line in headers)
            {
                var colon = line.IndexOf(':', StringComparison.Ordinal);
                response.Headers.TryAddWithoutValidation(line[..colon].Trim(), line[(colon + 1)..].Trim());
            }

            return Task.FromResult(response);
        });
        return this;
    }

    /// <summary>Queues a transport failure.</summary>
    public StubGitHubHandler Throw(Exception exception)
    {
        _steps.Enqueue((_, _) => Task.FromException<HttpResponseMessage>(exception));
        return this;
    }

    /// <summary>Queues a request that never answers; it ends with <see cref="TaskCanceledException"/> when the request's token fires.</summary>
    public StubGitHubHandler Hang()
    {
        _steps.Enqueue(async (_, ct) =>
        {
            var tcs = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = ct.Register(() => tcs.TrySetCanceled(ct));
            return await tcs.Task.ConfigureAwait(false);
        });
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);
        Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, headers, null));
        Tokens.Add(cancellationToken);
        if (_steps.TryDequeue(out var step))
        {
            return step(request, cancellationToken);
        }

        Violations.Add("unscripted: " + request.Method + " " + request.RequestUri);
        return Task.FromResult(new HttpResponseMessage((HttpStatusCode)599) { Content = new StringContent(string.Empty) });
    }
}
