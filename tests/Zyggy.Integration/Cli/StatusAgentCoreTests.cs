using Zyggy.Cli.Commands;
using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;
using Zyggy.Core.Health;
using Zyggy.Core.Tenancy;

namespace Zyggy.Integration.Cli;

/// <summary>Spec 15 AC-12: the node line of <c>zyggy status</c> names the <c>zyggy-core</c> commit the node reports, twelve characters.</summary>
public sealed class StatusAgentCoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);

    private static readonly NodeConfiguration Configuration = new(
        TenantId.Parse("acme"),
        MachineName.Parse("home-laptop"),
        null,
        new BusSettings("acme-org", "acme-bus", "main", "/tmp/bus", "https://example.invalid/acme-org/acme-bus.git"),
        new PollSettings(),
        new SecretsSettings());

    private static readonly InventoryReport Empty = new(null, [], [], []);

    private static readonly HealthVerdict Green = new(HealthLevel.Green, [], []);

    private static NodeHealth Health(string? agentCoreSha) =>
        new("0.18.0", "acme", "home-laptop", Now, null, Now, "0123", 0, 0, null, 4999, null, true, agentCoreSha);

    [Fact]
    public void RenderOverview_AgentCoreSha_NodeLineNamesTwelveCharacters()
    {
        // Act
        string first = StatusCommand.RenderOverview(Configuration, Health("89abcdef0123456789abcdef0123456789abcdef"), Green, Empty, Now).Split('\n')[0];

        // Assert
        first.Should().Be("node    home-laptop (acme) · zyggy-node 0.18.0 · zyggy-core 89abcdef0123 · health: green");
    }

    [Fact]
    public void RenderOverview_AgentCoreShaNull_NodeLineSaysUnknown()
    {
        // Act
        string first = StatusCommand.RenderOverview(Configuration, Health(null), Green, Empty, Now).Split('\n')[0];

        // Assert
        first.Should().Be("node    home-laptop (acme) · zyggy-node 0.18.0 · zyggy-core unknown · health: green");
    }

    [Fact]
    public void RenderOverview_NoNodeAnswering_NodeLineHasNoAgentCore()
    {
        // Arrange
        var down = new HealthVerdict(HealthLevel.Down, ["no node service answering on http://localhost:4711/health"], []);

        // Act
        string first = StatusCommand.RenderOverview(Configuration, null, down, Empty, Now).Split('\n')[0];

        // Assert
        first.Should().Be("node    home-laptop (acme) · health: down — no node service answering on http://localhost:4711/health");
    }
}
