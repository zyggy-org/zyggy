using Zyggy.Core.Brief;

namespace Zyggy.Core.Tests.Brief;

/// <summary>Spec 35 AC-17 and Contracts "Files": the item list beside the brief, what <c>zyggy brief items</c> will read.</summary>
public sealed class BriefSidecarTests
{
    [Fact]
    public void Write_ByteEqualsGolden()
    {
        // Act
        var bytes = BriefSidecar.From(BriefScenario.Document()).ToBytes();

        // Assert
        bytes.Should().Equal(File.ReadAllBytes(BriefFixture.Golden("brief-10-noideas.json")));
    }

    [Fact]
    public void Items_KindDestinationIdsSenderAddress()
    {
        // Act
        var sidecar = BriefSidecar.From(BriefScenario.Document());

        // Assert
        sidecar.Items.Select(i => (i.N, i.Kind)).Should().Equal((1, "send"), (2, "move"), (3, "move"), (4, "discard-draft"), (5, "file-other"));
        sidecar.Items[0].Sender!.Address.Should().Be("bob@example.org");
        sidecar.Items[0].DraftId.Should().Be("d02");
        sidecar.Items[2].Destination.Should().Be("deleteditems");
    }

    [Fact]
    public void FileOther_MessageIdsArray()
    {
        // Act
        var sidecar = BriefSidecar.From(BriefScenario.Document());

        // Assert
        sidecar.Items[4].MessageIds.Should().Equal("m05", "m06");
        sidecar.Items[4].MessageId.Should().BeNull();
        sidecar.Items[4].Sender.Should().BeNull();
    }

    [Fact]
    public void Counts_And_PageExceeded_Written_AndParsedBack()
    {
        // Arrange
        var document = BriefScenario.Document() with { PageExceeded = true };

        // Act
        var bytes = BriefSidecar.From(document).ToBytes();
        var parsed = BriefSidecar.Parse(bytes);

        // Assert
        parsed!.Counts.Should().Be(new SidecarCounts(1, 8, 2, 2, 1));
        parsed.PageExceeded.Should().BeTrue();
        parsed.Items.Should().HaveCount(5);
        System.Text.Encoding.UTF8.GetString(bytes).Should().Contain("\"page_exceeded\": true");
    }
}
