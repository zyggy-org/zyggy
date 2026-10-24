using System.Net;
using System.Net.Sockets;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Zyggy.Core.Health;
using Zyggy.Integration.Node;
using Zyggy.Node;

namespace Zyggy.Integration.Health;

/// <summary>Plan 08 Step 5 on the wire: the health client against the real <c>HealthServer</c> started by the production host on an ephemeral loopback port.</summary>
public sealed class HealthClientLiveTests
{
    [Fact]
    public async Task ReadAsync_RunningHost_ReachableTenantMachineAndLastPoll()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using NodeHostHarness harness = await NodeHostHarness.CreateAsync(ct);
        using IHost host = harness.Build();
        await host.StartAsync(ct);
        HealthServer server = host.Services.GetRequiredService<HealthServer>();
        NodeStatus status = host.Services.GetRequiredService<NodeStatus>();
        await NodeHostHarness.WaitUntilAsync(() => Task.FromResult(server.IsListening && status.Listener?.Status.LastPollUtc is not null), TimeSpan.FromSeconds(10), ct);
        using var client = new HealthClient(new Uri($"http://127.0.0.1:{server.BoundPort}/health"), TimeProvider.System);

        // Act
        HealthProbe probe = await client.ReadAsync(ct);
        await host.StopAsync(ct);

        // Assert
        NodeHealth health = probe.Should().BeOfType<Reachable>().Which.Health;
        health.Tenant.Should().Be("acme");
        health.Machine.Should().Be("home-laptop");
        health.LastPollUtc.Should().NotBeNull().And.BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(10));
        health.Version.Should().Be(NodeHost.InformationalVersion());
    }

    [Fact]
    public async Task ReadAsync_ClosedLoopbackPort_UnreachableConnectionRefused()
    {
        // Arrange: a port nobody listens on
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var client = new HealthClient(new Uri($"http://127.0.0.1:{port}/health"), TimeProvider.System);

        // Act
        HealthProbe probe = await client.ReadAsync(TestContext.Current.CancellationToken);

        // Assert: Windows retries a refused loopback connect and may run into the 2 s client timeout first; both are "nothing answers"
        probe.Should().BeOfType<Unreachable>().Which.Detail.Should().BeOneOf("connection refused", "timeout");
    }

    [Fact]
    public async Task ReadAsync_WrongPath_UnreachableHttp404()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using NodeHostHarness harness = await NodeHostHarness.CreateAsync(ct);
        using IHost host = harness.Build();
        await host.StartAsync(ct);
        HealthServer server = host.Services.GetRequiredService<HealthServer>();
        await NodeHostHarness.WaitUntilAsync(() => Task.FromResult(server.IsListening), TimeSpan.FromSeconds(10), ct);
        using var client = new HealthClient(new Uri($"http://127.0.0.1:{server.BoundPort}/healthz"), TimeProvider.System);

        // Act
        HealthProbe probe = await client.ReadAsync(ct);
        await host.StopAsync(ct);

        // Assert
        probe.Should().Be(new Unreachable("HTTP 404"));
    }
}
