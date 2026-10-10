using Zyggy.Core.Memory;

namespace Zyggy.Core.Tests.Memory;

public sealed class ArchiveRefusalWireTests
{
    [Theory]
    [InlineData(ArchiveRefusal.Unattended, "unattended")]
    [InlineData(ArchiveRefusal.SourceRefused, "source_refused")]
    [InlineData(ArchiveRefusal.TypeRefused, "type_refused")]
    [InlineData(ArchiveRefusal.TypeNotAllowed, "type_not_allowed")]
    [InlineData(ArchiveRefusal.Empty, "empty")]
    [InlineData(ArchiveRefusal.TooLarge, "too_large")]
    [InlineData(ArchiveRefusal.ProjectCap, "project_cap")]
    [InlineData(ArchiveRefusal.TotalCap, "total_cap")]
    [InlineData(ArchiveRefusal.SecretPattern, "secret_pattern")]
    [InlineData(ArchiveRefusal.ContactDetail, "contact_detail")]
    [InlineData(ArchiveRefusal.SlugTaken, "slug_taken")]
    [InlineData(ArchiveRefusal.NotFound, "not_found")]
    public void ToWire_Member_ReturnsSnakeCase(ArchiveRefusal refusal, string wire)
    {
        // Act
        var actual = ArchiveRefusalWire.ToWire(refusal);

        // Assert
        actual.Should().Be(wire);
        ArchiveRefusalWire.TryFromWire(wire, out var back).Should().BeTrue();
        back.Should().Be(refusal);
    }

    [Fact]
    public void TryFromWire_Unknown_False()
    {
        // Act & Assert
        ArchiveRefusalWire.TryFromWire("nope", out _).Should().BeFalse();
        ArchiveRefusalWire.TryFromWire(null, out _).Should().BeFalse();
    }

    [Fact]
    public void EveryMember_HasATestRow()
    {
        // Assert: the theory above lists twelve members.
        Enum.GetValues<ArchiveRefusal>().Should().HaveCount(12);
    }
}
