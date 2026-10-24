using Zyggy.Core.Configuration;
using Zyggy.Core.Envelope;
using Zyggy.Core.Policy;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Tests.Policy;

/// <summary>Plan 06 Step 3 (AC-15): the v1 <see cref="IPolicySource"/> projected from <c>node.json</c>.</summary>
public sealed class NodeJsonPolicySourceTests
{
    internal static NodeConfiguration Configuration() => new(
        TenantId.Parse("acme"),
        MachineName.Parse("home-laptop"),
        KeyId.Parse("acme/1"),
        new BusSettings("acme-org", "acme-bus", "main", "/srv/bus", "https://example.invalid/acme-org/acme-bus.git"),
        new PollSettings(),
        new SecretsSettings());

    [Fact]
    public async Task GetAsync_FromConfiguration_ProjectsEveryField()
    {
        // Arrange
        var configuration = Configuration() with
        {
            Claude = new ClaudeSettings(MaxTurns: 40, DefaultTimeoutMinutes: 15, DefaultAllowedTools: ["Read", "Edit"], MaxBudgetUsd: 5m),
            DevRoots = ["/src"],
        };

        // Act
        var policy = await new NodeJsonPolicySource(configuration).GetAsync(TestContext.Current.CancellationToken);

        // Assert
        policy.DefaultAllowedTools.Should().Equal("Read", "Edit");
        policy.DefaultTimeoutMinutes.Should().Be(15);
        policy.MaxTurns.Should().Be(40);
        policy.MaxBudgetUsd.Should().Be(5m);
        policy.DevRoots.Should().Equal("/src");
    }

    [Fact]
    public async Task GetAsync_ClaudeAbsent_SpecDefaults()
    {
        // Act
        var policy = await new NodeJsonPolicySource(Configuration()).GetAsync(TestContext.Current.CancellationToken);

        // Assert
        policy.DefaultAllowedTools.Should().Equal(NodePolicy.Default.DefaultAllowedTools);
        policy.Should().BeEquivalentTo(NodePolicy.Default);
    }

    [Fact]
    public async Task GetAsync_ProjectsMaxConcurrentJobsAndWorkHours()
    {
        // Arrange: spec 07 — the §6 concurrency inputs come from node.json
        var configuration = Configuration() with
        {
            MaxConcurrentJobs = 3,
            WorkHours = new WorkHoursSettings("Europe/Brussels", ["Mon", "Fri"], "09:00", "17:30"),
        };

        // Act
        var policy = await new NodeJsonPolicySource(configuration).GetAsync(TestContext.Current.CancellationToken);

        // Assert
        policy.MaxConcurrentJobs.Should().Be(3);
        policy.WorkHours.Should().NotBeNull();
        policy.WorkHours!.Days.Should().BeEquivalentTo([DayOfWeek.Monday, DayOfWeek.Friday]);
        policy.WorkHours.From.Should().Be(new TimeOnly(9, 0));
        policy.WorkHours.To.Should().Be(new TimeOnly(17, 30));
        policy.WorkHours.Zone.BaseUtcOffset.Should().Be(TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task GetAsync_WorkHoursAbsent_NullMeansAlwaysInside()
    {
        // Act
        var policy = await new NodeJsonPolicySource(Configuration()).GetAsync(TestContext.Current.CancellationToken);

        // Assert
        policy.WorkHours.Should().BeNull();
        policy.MaxConcurrentJobs.Should().Be(2);
    }

    [Fact]
    public async Task GetAsync_DevRoots_ReadNotEnforced()
    {
        // Arrange: O18 — the roots are carried for deliverable 14; nothing compares a project against them in 06
        var configuration = Configuration() with { DevRoots = ["/src", "/work"] };

        // Act
        var policy = await new NodeJsonPolicySource(configuration).GetAsync(TestContext.Current.CancellationToken);

        // Assert
        policy.DevRoots.Should().Equal("/src", "/work");
    }
}
