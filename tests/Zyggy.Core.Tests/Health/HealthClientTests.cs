using System.Net;
using System.Text;

using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Health;

namespace Zyggy.Core.Tests.Health;

/// <summary>Plan 08 Step 5: the one HTTP client of the CLI path reads <c>/health</c> on loopback only, bypasses the proxy, and names every failure without copying a message.</summary>
public sealed class HealthClientTests
{
    private static readonly Uri Local = new("http://localhost:4711/health");

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 24, 10, 0, 0, TimeSpan.Zero));

    [Theory]
    [InlineData("http://example.invalid:4711/health")]
    [InlineData("http://10.0.0.5:4711/health")]
    public void Ctor_NonLoopbackHost_ArgumentExceptionAndNoRequest(string url)
    {
        // Arrange
        var stub = new StubHealthHandler();

        // Act
        Action act = () => _ = new HealthClient(new Uri(url), _clock, stub);

        // Assert
        act.Should().Throw<ArgumentException>().WithParameterName("healthUrl");
        stub.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("http://localhost:4711/health")]
    [InlineData("http://127.0.0.1:4711/health")]
    [InlineData("http://[::1]:4711/health")]
    public void Ctor_LoopbackForms_Accepted(string url)
    {
        // Act
        using var client = new HealthClient(new Uri(url), _clock, new StubHealthHandler());

        // Assert
        client.Url.Should().Be(new Uri(url));
    }

    [Fact]
    public async Task ReadAsync_TwoHundredJson_ReachableWithFields()
    {
        // Arrange
        var stub = new StubHealthHandler().Respond(200, NodeHealthJson.Serialize(NodeHealthJsonTests.Known));
        using var client = new HealthClient(Local, _clock, stub);

        // Act
        HealthProbe probe = await client.ReadAsync(TestContext.Current.CancellationToken);

        // Assert
        probe.Should().BeOfType<Reachable>().Which.Health.Should().Be(NodeHealthJsonTests.Known);
        stub.Requests.Should().ContainSingle().Which.Should().Be("GET " + Local);
    }

    [Fact]
    public async Task ReadAsync_FiveHundred_UnreachableHttp500()
    {
        // Arrange
        using var client = new HealthClient(Local, _clock, new StubHealthHandler().Respond(500, "boom"));

        // Act
        HealthProbe probe = await client.ReadAsync(TestContext.Current.CancellationToken);

        // Assert
        probe.Should().Be(new Unreachable("HTTP 500"));
    }

    [Fact]
    public async Task ReadAsync_NonJsonBody_UnreachableUnparsable()
    {
        // Arrange
        using var client = new HealthClient(Local, _clock, new StubHealthHandler().Respond(200, "<html>not json</html>"));

        // Act
        HealthProbe probe = await client.ReadAsync(TestContext.Current.CancellationToken);

        // Assert
        probe.Should().Be(new Unreachable("unparsable body"));
    }

    [Fact]
    public async Task ReadAsync_HandlerThrowsHttpRequestException_UnreachableConnectionRefused()
    {
        // Arrange: the exception's message (which could carry a host detail or a token) is never copied
        const string token = "zyggy-fake-token-71a2";
        using var client = new HealthClient(Local, _clock, new StubHealthHandler().Throw(new HttpRequestException("refused by " + token)));

        // Act
        HealthProbe probe = await client.ReadAsync(TestContext.Current.CancellationToken);

        // Assert
        probe.Should().Be(new Unreachable("connection refused"));
        ((Unreachable)probe).Detail.Should().NotContain(token);
    }

    [Fact]
    public async Task ReadAsync_HandlerNeverAnswers_UnreachableTimeout()
    {
        // Arrange: the stub awaits the request's token; the client's own 2 s timeout fires
        using var client = new HealthClient(Local, _clock, new StubHealthHandler().Hang());
        var started = DateTime.UtcNow;

        // Act
        HealthProbe probe = await client.ReadAsync(TestContext.Current.CancellationToken);

        // Assert
        probe.Should().Be(new Unreachable("timeout"));
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ReadAsync_CallerCancelled_Throws()
    {
        // Arrange
        using var client = new HealthClient(Local, _clock, new StubHealthHandler().Hang());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Act
        Func<Task> act = () => client.ReadAsync(cts.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void CreateHandler_UseProxyFalseAndNoRedirects()
    {
        // Act: plan A10 — the production handler, inspected directly
        using SocketsHttpHandler handler = HealthClient.CreateHandler();

        // Assert
        handler.UseProxy.Should().BeFalse();
        handler.AllowAutoRedirect.Should().BeFalse();
    }

    /// <summary>A handler scripted for one request: a response, a transport failure, or a hang until the request's token fires.</summary>
    private sealed class StubHealthHandler : HttpMessageHandler
    {
        private Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _step = (_, _) =>
            Task.FromResult(new HttpResponseMessage((HttpStatusCode)599));

        public List<string> Requests { get; } = [];

        public StubHealthHandler Respond(int status, string body)
        {
            _step = (_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
            return this;
        }

        public StubHealthHandler Throw(Exception exception)
        {
            _step = (_, _) => Task.FromException<HttpResponseMessage>(exception);
            return this;
        }

        public StubHealthHandler Hang()
        {
            _step = async (_, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                throw new InvalidOperationException("unreachable");
            };
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.Method + " " + request.RequestUri);
            return _step(request, cancellationToken);
        }
    }
}
