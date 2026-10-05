using Zyggy.Core.M365.Runs;

namespace Zyggy.Core.Tests.M365;

/// <summary>Spec 33 AC-34: the m365 model-run verbs refuse a binary whose version or hash differs from <c>instance/zyggy.json</c> (the dream's rule).</summary>
public sealed class BinaryPinTests
{
    private const string Pin = """{ "version": "0.2.0", "sha256": { "linux-x64": "abc123" } }""";

    [Fact]
    public void Check_NoInstanceDir_Skipped()
    {
        // Act
        var error = BinaryPin.Check(null, "0.2.0", "zzz", "linux-x64", _ => null);

        // Assert
        error.Should().BeNull();
    }

    [Fact]
    public void Check_Match_Proceeds()
    {
        // Act
        var error = BinaryPin.Check("/srv/instance", "0.2.0+sha.1", "ABC123", "linux-x64", _ => Pin);

        // Assert
        error.Should().BeNull();
    }

    [Theory]
    [InlineData("0.2.1", "abc123", "configuration error: version_mismatch: version 0.2.1 is not the pinned 0.2.0")]
    [InlineData("0.2.0", "def456", "configuration error: version_mismatch: sha256 of the binary is not the pinned one for linux-x64")]
    public void Check_Mismatch_ExitThreeVersionMismatch(string version, string hash, string message)
    {
        // Act
        var error = BinaryPin.Check("/srv/instance", version, hash, "linux-x64", _ => Pin);

        // Assert
        error.Should().Be(message);
    }

    [Fact]
    public void Check_PinMissing_Error()
    {
        // Act
        var error = BinaryPin.Check("/srv/instance", "0.2.0", "abc123", "linux-x64", _ => null);

        // Assert
        error.Should().StartWith("configuration error: ").And.Contain("zyggy.json");
    }
}
