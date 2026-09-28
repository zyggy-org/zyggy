using Microsoft.Extensions.Time.Testing;

using NSubstitute;

namespace Zyggy.Core.Tests.Infrastructure;

/// <summary>A seam used only to prove that NSubstitute can substitute interfaces under xunit.v3.</summary>
public interface ITestSeam
{
    void Ping(string value);
}

public sealed class TestStackSmokeTests
{
    [Fact]
    public void Be_FailingAssertion_SurfacesAsXunitException()
    {
        // Arrange
        Action act = () => 1.Should().Be(2);

        // Act & Assert (FluentAssertions must have detected xunit.v3, not fall back to its own exception)
        act.Should().Throw<Xunit.Sdk.XunitException>();
    }

    [Fact]
    public void GetUtcNow_AfterAdvance_ReturnsAdvancedTime()
    {
        // Arrange
        var start = new DateTimeOffset(2026, 9, 28, 10, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(start);

        // Act
        clock.Advance(TimeSpan.FromMinutes(5));

        // Assert
        clock.GetUtcNow().Should().Be(start + TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void Received_AfterCallOnSubstitute_RecordsTheCall()
    {
        // Arrange
        var seam = Substitute.For<ITestSeam>();

        // Act
        seam.Ping("x");

        // Assert
        seam.Received(1).Ping("x");
    }
}
