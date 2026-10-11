using Zyggy.Core.Memory;

namespace Zyggy.Core.Tests.Memory;

/// <summary>Spec 38 AC-14: the commit of one offloaded item, byte-equal to the hand-written golden.</summary>
public sealed class OffloadCommitMessageTests
{
    [Fact]
    public void For_FixedInputs_EqualsGolden()
    {
        // Act
        var message = OffloadCommitMessage.For(
            Slug.Parse("zyggy"), Slug.Parse("quote-2026"), "txt", "01ITEMQUOTE",
            "c5f2cab5d3b4d46c44bee6f1ee037d471c419b739908f4a05251745fb4794f88", "jAt95bF64I8MSmyO3joPMtROLIU=", 68);

        // Assert
        message.Should().Be(OffloadFixture.Golden("offload/commit-message-offload.txt"));
    }
}
