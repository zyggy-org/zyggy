using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Policy;

namespace Zyggy.Core.Tests.Policy;

/// <summary>Plan 06 Step 3 (AC-25, first half; Assumption A1): <c>AddNodeJsonPolicy</c> resolves over a registered <c>NodeConfiguration</c>, no hidden default.</summary>
public sealed class PolicyRegistrationTests
{
    [Fact]
    public void AddNodeJsonPolicy_WithNodeConfiguration_ResolvesSingletonIPolicySource()
    {
        // Arrange
        var services = new ServiceCollection().AddSingleton(NodeJsonPolicySourceTests.Configuration()).AddNodeJsonPolicy();
        using var provider = services.BuildServiceProvider();

        // Act
        var first = provider.GetRequiredService<IPolicySource>();
        var second = provider.GetRequiredService<IPolicySource>();

        // Assert
        first.Should().BeOfType<NodeJsonPolicySource>().And.BeSameAs(second);
    }

    [Fact]
    public void AddNodeJsonPolicy_WithoutNodeConfiguration_ResolvingThrowsInvalidOperationException()
    {
        // Arrange
        using var provider = new ServiceCollection().AddNodeJsonPolicy().BuildServiceProvider();

        // Act
        var act = () => provider.GetRequiredService<IPolicySource>();

        // Assert
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void AddNodeJsonPolicy_ReturnsSameCollection()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var returned = services.AddNodeJsonPolicy();

        // Assert
        returned.Should().BeSameAs(services);
    }
}
