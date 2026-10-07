using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.LinkedIn;

/// <summary>The <c>zyggy linkedin</c> dispatcher: an unknown or missing verb is exit 4 with the usage.</summary>
public sealed class LinkedInVerbHostTests : IDisposable
{
    private const string Usage = " (usage: zyggy linkedin <auth start|auth finish|auth status|mcp-server>)\n";

    private readonly LinkedInFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Theory]
    [InlineData(new[] { "publish" }, "publish")]
    [InlineData(new[] { "auth" }, "auth")]
    [InlineData(new[] { "auth", "set-token", "x" }, "auth set-token")]
    public async Task UnknownVerb_ExitFour(string[] args, string named)
    {
        // Arrange
        var console = new VerbConsole();

        // Act
        var exit = await _fixture.Host().RunAsync(args, console.Io, CancellationToken.None);

        // Assert
        exit.Should().Be(4);
        console.Stderr.Should().Be($"linkedin: unknown verb '{named}'" + Usage);
        console.Stdout.Should().BeEmpty();
    }

    [Fact]
    public async Task NoVerb_ExitFour()
    {
        // Arrange
        var console = new VerbConsole();

        // Act
        var exit = await _fixture.Host().RunAsync([], console.Io, CancellationToken.None);

        // Assert
        exit.Should().Be(4);
        console.Stderr.Should().Be("linkedin: no verb given" + Usage);
    }
}
