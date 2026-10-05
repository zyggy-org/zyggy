using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.M365;

/// <summary>
/// The m365 binary tests stay inside their fixture: a CI runner's <c>XDG_CONFIG_HOME</c> once placed a test key in the runner's own
/// config directory, where another binary test found it and minted against the real login host. Every key location is pinned here.
/// </summary>
public sealed class M365InstanceFixtureTests : IDisposable
{
    private readonly M365InstanceFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Theory]
    [InlineData("HOME")]
    [InlineData("XDG_CONFIG_HOME")]
    [InlineData("XDG_STATE_HOME")]
    [InlineData("ZYGGY_STATE_DIR")]
    [InlineData("ZYGGY_INSTANCE_DIR")]
    public void Env_EveryKeyAndStateLocation_InsideTheFixture(string variable)
    {
        // Act
        var value = _fixture.Env()[variable];

        // Assert
        value.Should().StartWith(_fixture.Root);
    }

    [Fact]
    public void Env_CredentialsDirectory_Removed()
    {
        // Act
        var env = _fixture.Env();

        // Assert
        env.Should().ContainKey("CREDENTIALS_DIRECTORY").WhoseValue.Should().BeNull();
    }
}
