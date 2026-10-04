using Zyggy.Core.Dream;

namespace Zyggy.Core.Tests.Dream;

/// <summary>AC-37 (unit): the binary refuses to dream unless its version and file hash match <c>instance/zyggy.json</c>.</summary>
public sealed class VersionPinTests
{
    private const string Hash = "446196658c6899256ff370be2a7f79c860ec650006daf6a46d37afd49ee8c3c6";
    private const string Pin = $$"""{ "version": "1.2.3", "sha256": { "linux-x64": "{{Hash}}" } }""";

    [Fact]
    public void Check_Match()
    {
        // Act
        var result = VersionPin.Check(Pin, "1.2.3+0123abcd", Hash, "linux-x64");

        // Assert
        result.Status.Should().Be(VersionPinStatus.Match);
    }

    [Fact]
    public void Check_VersionDiffers_Mismatch()
    {
        // Act
        var result = VersionPin.Check(Pin, "1.2.4", Hash, "linux-x64");

        // Assert
        result.Status.Should().Be(VersionPinStatus.Mismatch);
        result.Detail.Should().Contain("1.2.4").And.Contain("1.2.3");
    }

    [Fact]
    public void Check_HashDiffers_Mismatch()
    {
        // Act
        var result = VersionPin.Check(Pin, "1.2.3", new string('0', 64), "linux-x64");

        // Assert
        result.Status.Should().Be(VersionPinStatus.Mismatch);
        result.Detail.Should().Contain("sha256");
    }

    [Fact]
    public void Check_RidMissing_Mismatch()
    {
        // Act
        var result = VersionPin.Check(Pin, "1.2.3", Hash, "win-x64");

        // Assert
        result.Status.Should().Be(VersionPinStatus.Mismatch);
        result.Detail.Should().Contain("win-x64");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{ \"version\": 3 }")]
    [InlineData("[]")]
    public void Check_BadJson_Invalid(string json)
    {
        // Act
        var result = VersionPin.Check(json, "1.2.3", Hash, "linux-x64");

        // Assert
        result.Status.Should().Be(VersionPinStatus.Invalid);
    }
}
