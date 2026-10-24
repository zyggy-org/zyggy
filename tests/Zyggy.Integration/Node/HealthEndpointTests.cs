using System.Net;
using System.Net.Sockets;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Node;

namespace Zyggy.Integration.Node;

/// <summary>Spec 07 AC-14 and the login probe: <c>GET /health</c> on loopback through the real host, the §11 body, the auth line, the port-in-use refusal.</summary>
public sealed class HealthEndpointTests
{
    [Fact]
    public async Task GetHealth_RunningHost_TwoHundredJsonFieldsAndLoopbackOnly()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using NodeHostHarness harness = await NodeHostHarness.CreateAsync(ct);
        using IHost host = harness.Build();
        await host.StartAsync(ct);
        HealthServer server = host.Services.GetRequiredService<HealthServer>();
        NodeStatus status = host.Services.GetRequiredService<NodeStatus>();
        await NodeHostHarness.WaitUntilAsync(() => Task.FromResult(server.IsListening && status.Listener?.Status.LastPollUtc is not null && status.ClaudeAuthOk == true), TimeSpan.FromSeconds(10), ct);
        using HttpClient client = Client(server);

        // Act
        using HttpResponseMessage response = await client.GetAsync(new Uri("/health", UriKind.Relative), ct);
        string body = await response.Content.ReadAsStringAsync(ct);
        await host.StopAsync(ct);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        using JsonDocument document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("tenant").GetString().Should().Be("acme");
        document.RootElement.GetProperty("machine").GetString().Should().Be("home-laptop");
        document.RootElement.GetProperty("version").GetString().Should().Be(NodeHost.InformationalVersion());
        document.RootElement.GetProperty("lastPollUtc").GetString().Should().MatchRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$");
        document.RootElement.GetProperty("jobsRunning").GetInt32().Should().Be(0);
        document.RootElement.GetProperty("claudeAuthOk").GetBoolean().Should().BeTrue();
        document.RootElement.EnumerateObject().Should().HaveCount(14);
        server.Addresses.Should().NotBeEmpty().And.AllSatisfy(a => new Uri(a).Host.Should().BeOneOf("127.0.0.1", "[::1]", "::1"));
        SecretSweep.AssertAbsent([body]);
    }

    [Theory]
    [InlineData("GET", "/", HttpStatusCode.NotFound)]
    [InlineData("GET", "/healthx", HttpStatusCode.NotFound)]
    [InlineData("POST", "/health", HttpStatusCode.MethodNotAllowed)]
    [InlineData("DELETE", "/health", HttpStatusCode.MethodNotAllowed)]
    public async Task OtherPathsAndMethods_404And405(string method, string path, HttpStatusCode expected)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using NodeHostHarness harness = await NodeHostHarness.CreateAsync(ct);
        using IHost host = harness.Build();
        await host.StartAsync(ct);
        HealthServer server = host.Services.GetRequiredService<HealthServer>();
        await NodeHostHarness.WaitUntilAsync(() => Task.FromResult(server.IsListening), TimeSpan.FromSeconds(10), ct);
        using HttpClient client = Client(server);
        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(path, UriKind.Relative));

        // Act
        using HttpResponseMessage response = await client.SendAsync(request, ct);
        await host.StopAsync(ct);

        // Assert
        response.StatusCode.Should().Be(expected);
    }

    [Fact]
    public async Task Health_NeverBoundAfterRefusal()
    {
        // Arrange: KeyMissing — the start-up check fails, the server must never bind
        var ct = TestContext.Current.CancellationToken;
        await using NodeHostHarness harness = await NodeHostHarness.CreateAsync(ct, seedSecrets: false);
        HealthServer? server = null;

        // Act
        int exitCode = await harness.RunAsync(TimeSpan.FromSeconds(20), ct, s => s.AddSingleton(sp =>
        {
            server = ActivatorUtilities.CreateInstance<HealthServer>(sp);
            return server;
        }));

        // Assert
        exitCode.Should().Be(6);
        server.Should().NotBeNull();
        server!.IsListening.Should().BeFalse();
        server.BoundPort.Should().Be(0);
        harness.LogLines.Should().NotContain(l => l.Contains("health endpoint listening", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Health_AuthProbe_LogsClaudeAuthOkOnce()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using NodeHostHarness harness = await NodeHostHarness.CreateAsync(ct);

        // Act: run until the probe has answered (under full-suite load the fake's `auth status` can take seconds to spawn)
        int exitCode = await harness.RunUntilAsync(() => Task.FromResult(harness.LogLines.Any(l => l.EndsWith("claude auth ok", StringComparison.Ordinal))), TimeSpan.FromSeconds(30), ct);

        // Assert
        exitCode.Should().Be(0, string.Join(Environment.NewLine, harness.LogLines));
        harness.LogLines.Where(l => l.EndsWith("claude auth ok", StringComparison.Ordinal)).Should().ContainSingle();
        harness.LogLines.Where(l => l.Contains("health endpoint listening on http://localhost:", StringComparison.Ordinal)).Should().ContainSingle();
    }

    [Fact]
    public async Task Health_PortInUse_ExitSix()
    {
        // Arrange: the test holds a loopback port; the host is told to use it
        var ct = TestContext.Current.CancellationToken;
        await using NodeHostHarness harness = await NodeHostHarness.CreateAsync(ct);
        using var holder = new TcpListener(IPAddress.Loopback, 0);
        holder.Start();
        int port = ((IPEndPoint)holder.LocalEndpoint).Port;

        // Act
        int exitCode = await harness.RunAsync(TimeSpan.FromSeconds(20), ct, s => s.Configure<NodeHostOptions>(o => o.HealthPort = port));

        // Assert
        exitCode.Should().Be(6);
        harness.LogLines.Where(l => l.Contains("[ERR]", StringComparison.Ordinal)).Should().ContainSingle()
            .Which.Should().Contain($"health port {port} in use").And.Contain("Node: refused to start");
    }

    private static HttpClient Client(HealthServer server) => new() { BaseAddress = new Uri($"http://127.0.0.1:{server.BoundPort}/") };
}
