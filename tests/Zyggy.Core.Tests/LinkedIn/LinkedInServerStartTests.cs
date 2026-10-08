using Zyggy.Core.LinkedIn;

namespace Zyggy.Core.Tests.LinkedIn;

/// <summary>The server's start rule (spec 36 AC-1, AC-6): unattended → 5 before anything is read; configuration → 3; switched off → no tool.</summary>
public sealed class LinkedInServerStartTests : IDisposable
{
    private readonly LinkedInFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void Decide_HooksOff_ExitFiveBeforeConfigRead()
    {
        // Arrange
        File.Delete(_fixture.InstanceFile);
        _fixture.Environment["ZYGGY_HOOKS"] = "off";

        // Act
        var start = Decide();

        // Assert
        start.Should().Be(new ServerStart(5, "linkedin: refused in an unattended run", false, null));
    }

    [Fact]
    public void Decide_ConfigMissing_ExitThree()
    {
        // Arrange
        File.Delete(_fixture.InstanceFile);

        // Act
        var start = Decide();

        // Assert
        start.Exit.Should().Be(3);
        start.Message.Should().Be($"linkedin: configuration error: {_fixture.InstanceFile} is missing");
        start.OfferTool.Should().BeFalse();
    }

    [Fact]
    public void Decide_PrincipalMissing_ExitThree()
    {
        // Arrange
        _fixture.Environment.Remove("ZYGGY_TENANT");

        // Act
        var start = Decide();

        // Assert
        start.Exit.Should().Be(3);
        start.Message.Should().Be("linkedin: configuration error: ZYGGY_TENANT is not set");
    }

    [Fact]
    public void Decide_EnabledEmpty_NoTool()
    {
        // Arrange
        _fixture.WriteInstance("""{"client_id":"abc","redirect_uri":"https://localhost/cb","actions":{"enabled":[]}}""");

        // Act
        var start = Decide();

        // Assert
        start.Exit.Should().BeNull();
        start.OfferTool.Should().BeFalse();
        start.Config.Should().NotBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("on")]
    [InlineData("")]
    public void Decide_Default_OfferTool(string? hooks)
    {
        // Arrange
        _fixture.Environment["ZYGGY_HOOKS"] = hooks;

        // Act
        var start = Decide();

        // Assert
        start.Exit.Should().BeNull();
        start.OfferTool.Should().BeTrue();
        start.Config!.PostMaxChars.Should().Be(3000);
    }

    private ServerStart Decide() => LinkedInServerStart.Decide(_fixture.Environment, LinkedInFixture.FindTimeZone);
}
