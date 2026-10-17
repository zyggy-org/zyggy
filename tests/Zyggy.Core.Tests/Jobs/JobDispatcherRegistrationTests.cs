using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;
using Zyggy.Core.Jobs;
using Zyggy.Core.Policy;
using Zyggy.Core.Tests.Policy;

namespace Zyggy.Core.Tests.Jobs;

/// <summary>Plan 07 Step 3 (Assumption A6): <c>AddJobDispatcher</c> resolves the dispatcher, its gate and the mode selector over the node's policy and configuration; no hidden default.</summary>
public sealed class JobDispatcherRegistrationTests
{
    private static readonly WorkHoursSettings Brussels = new("Europe/Brussels", ["Mon", "Tue", "Wed", "Thu", "Fri"], "08:00", "19:00");

    [Fact]
    public void AddJobDispatcher_WithPolicyAndConfiguration_ResolvesDispatcherGateAndModeSelector()
    {
        // Arrange
        var configuration = NodeJsonPolicySourceTests.Configuration() with { WorkHours = Brussels };
        using var provider = new ServiceCollection().AddSingleton(configuration).AddNodeJsonPolicy().AddJobDispatcher().BuildServiceProvider();

        // Act
        var dispatcher = provider.GetRequiredService<JobDispatcher>();
        var gate = provider.GetRequiredService<JobGate>();
        var selector = provider.GetRequiredService<PollModeSelector>();

        // Assert
        gate.Target.Should().BeSameAs(dispatcher);
        provider.GetRequiredService<JobDispatcher>().Should().BeSameAs(dispatcher);
        selector(new DateTimeOffset(2026, 10, 18, 10, 0, 0, TimeSpan.Zero)).Should().Be(PollMode.Idle, "Sunday is outside the window");
        selector(new DateTimeOffset(2026, 10, 19, 10, 0, 0, TimeSpan.Zero)).Should().Be(PollMode.Active);
    }

    [Fact]
    public void AddJobDispatcher_WithoutIPolicySource_ResolvingThrowsInvalidOperationException()
    {
        // Arrange
        using var provider = new ServiceCollection().AddSingleton(NodeJsonPolicySourceTests.Configuration()).AddJobDispatcher().BuildServiceProvider();

        // Act
        var act = () => provider.GetRequiredService<JobDispatcher>();

        // Assert
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void AddJobDispatcher_WorkHoursAbsent_SelectorAlwaysActive()
    {
        // Arrange
        using var provider = new ServiceCollection().AddSingleton(NodeJsonPolicySourceTests.Configuration()).AddNodeJsonPolicy().AddJobDispatcher().BuildServiceProvider();

        // Act
        var selector = provider.GetRequiredService<PollModeSelector>();

        // Assert
        selector(new DateTimeOffset(2026, 10, 18, 3, 0, 0, TimeSpan.Zero)).Should().Be(PollMode.Active);
    }
}
