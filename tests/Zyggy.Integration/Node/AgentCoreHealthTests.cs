using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Zyggy.Node;

namespace Zyggy.Integration.Node;

/// <summary>
/// Spec 15 AC-12 through the real host and the real <c>git</c>: <c>/health</c> <c>agentCoreSha</c> is the <c>HEAD</c> of the
/// <c>agentCore.path</c> checkout, follows it at the discovery interval, and is <c>null</c> with one warning when the folder is no checkout.
/// </summary>
public sealed class AgentCoreHealthTests
{
    [Fact]
    public async Task Health_AgentCoreCheckout_ShaIsItsHead()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using NodeHostHarness harness = await NodeHostHarness.CreateAsync(ct);
        string checkout = harness.Scratch.File("zyggy-core");
        string head = await CommitAsync(harness, checkout, "one", ct);
        harness.WriteNodeJson(agentCorePath: checkout);
        using IHost host = harness.Build();
        await host.StartAsync(ct);
        HealthServer server = host.Services.GetRequiredService<HealthServer>();
        using HttpClient client = await ClientAsync(server, ct);

        // Act
        await NodeHostHarness.WaitUntilAsync(async () => await ShaAsync(client, ct) is not null, TimeSpan.FromSeconds(60), ct);
        string? sha = await ShaAsync(client, ct);
        await host.StopAsync(ct);

        // Assert
        sha.Should().Be(head).And.MatchRegex("^[0-9a-f]{40}$");
        harness.LogLines.Should().NotContain(l => l.Contains("agent-core:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Health_AgentCoreNewCommit_FollowedAtTheDiscoveryInterval()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using NodeHostHarness harness = await NodeHostHarness.CreateAsync(ct);
        harness.DiscoveryInterval = TimeSpan.FromMilliseconds(500);
        string checkout = harness.Scratch.File("zyggy-core");
        string first = await CommitAsync(harness, checkout, "one", ct);
        harness.WriteNodeJson(agentCorePath: checkout);
        using IHost host = harness.Build();
        await host.StartAsync(ct);
        HealthServer server = host.Services.GetRequiredService<HealthServer>();
        using HttpClient client = await ClientAsync(server, ct);
        await NodeHostHarness.WaitUntilAsync(async () => await ShaAsync(client, ct) == first, TimeSpan.FromSeconds(60), ct);

        // Act
        string second = await CommitAsync(harness, checkout, "two", ct);
        await NodeHostHarness.WaitUntilAsync(async () => await ShaAsync(client, ct) == second, TimeSpan.FromSeconds(60), ct);
        await host.StopAsync(ct);

        // Assert
        second.Should().NotBe(first);
    }

    [Fact]
    public async Task Health_AgentCoreNotACheckout_NullAndOneWarningAcrossIntervals()
    {
        // Arrange: an existing folder that is no repository, read many times
        var ct = TestContext.Current.CancellationToken;
        await using NodeHostHarness harness = await NodeHostHarness.CreateAsync(ct);
        harness.DiscoveryInterval = TimeSpan.FromMilliseconds(200);
        string folder = harness.Scratch.File("not-a-checkout");
        Directory.CreateDirectory(folder);
        harness.WriteNodeJson(agentCorePath: folder);
        using IHost host = harness.Build();
        await host.StartAsync(ct);
        HealthServer server = host.Services.GetRequiredService<HealthServer>();
        using HttpClient client = await ClientAsync(server, ct);
        await NodeHostHarness.WaitUntilAsync(() => Task.FromResult(harness.LogLines.Any(l => l.Contains("agent-core:", StringComparison.Ordinal))), TimeSpan.FromSeconds(60), ct);
        int requests = harness.Head.Requests.Count;

        // Act: several poll seconds, so several discovery intervals
        await NodeHostHarness.WaitUntilAsync(() => Task.FromResult(harness.Head.Requests.Count >= requests + 3), TimeSpan.FromSeconds(60), ct);
        JsonElement health = await HealthAsync(client, ct);
        await host.StopAsync(ct);

        // Assert
        health.GetProperty("agentCoreSha").ValueKind.Should().Be(JsonValueKind.Null);
        harness.LogLines.Where(l => l.Contains("agent-core:", StringComparison.Ordinal)).Should().ContainSingle()
            .Which.Should().Contain("[WRN]").And.EndWith($"agent-core: {folder.Replace('\\', '/')} is not a git checkout");
    }

    /// <summary>Adds one commit to the repository at <paramref name="checkout"/> (created on first use) and returns its <c>HEAD</c>.</summary>
    private static async Task<string> CommitAsync(NodeHostHarness harness, string checkout, string content, CancellationToken ct)
    {
        if (!Directory.Exists(checkout))
        {
            Directory.CreateDirectory(checkout);
            await harness.Bus.RunGitAsync(checkout, ["init", "-q"], ct);
            await harness.Bus.RunGitAsync(checkout, ["symbolic-ref", "HEAD", "refs/heads/main"], ct);
            await harness.Bus.RunGitAsync(checkout, ["config", "user.name", "zyggy (test)"], ct);
            await harness.Bus.RunGitAsync(checkout, ["config", "user.email", "test@zyggy.org"], ct);
        }

        await File.WriteAllTextAsync(Path.Combine(checkout, "PROTOCOL.md"), content + "\n", ct);
        await harness.Bus.RunGitAsync(checkout, ["add", "-A"], ct);
        await harness.Bus.RunGitAsync(checkout, ["commit", "-q", "-m", content], ct);
        return (await harness.Bus.RunGitAsync(checkout, ["rev-parse", "HEAD"], ct)).Trim();
    }

    private static async Task<HttpClient> ClientAsync(HealthServer server, CancellationToken ct)
    {
        await NodeHostHarness.WaitUntilAsync(() => Task.FromResult(server.IsListening), TimeSpan.FromSeconds(60), ct);
        return new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.BoundPort}/") };
    }

    private static async Task<JsonElement> HealthAsync(HttpClient client, CancellationToken ct)
    {
        using JsonDocument document = JsonDocument.Parse(await client.GetStringAsync(new Uri("/health", UriKind.Relative), ct));
        return document.RootElement.Clone();
    }

    private static async Task<string?> ShaAsync(HttpClient client, CancellationToken ct) =>
        (await HealthAsync(client, ct)).GetProperty("agentCoreSha").GetString();
}
