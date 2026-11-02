using System.Text;

using Zyggy.Core.Envelope;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Envelope;

/// <summary>The bus item body: 76-column base64 with <c>=</c> padding, each line ended by <c>\n</c> (founding spec §4 W41-6).</summary>
public sealed class ItemBodyTests
{
    [Fact]
    public void Encode_ExactMultipleOf57Bytes_EveryLine76_LastLineEndsWithLf()
    {
        // Arrange: 57 bytes are exactly 76 base64 characters, so 114 bytes are two full lines
        byte[] file = new byte[114];
        new Random(41).NextBytes(file);

        // Act
        string body = Encoding.ASCII.GetString(ItemBody.Encode(file));

        // Assert
        body.Should().HaveLength(2 * 77).And.EndWith("\n");
        body.Split('\n').Should().Equal(Convert.ToBase64String(file)[..76], Convert.ToBase64String(file)[76..], string.Empty);
    }

    [Fact]
    public void Encode_OneByte_SingleLineWithPadding()
    {
        // Act
        byte[] body = ItemBody.Encode([0x00]);

        // Assert
        body.Should().Equal("AA==\n"u8.ToArray());
    }

    [Fact]
    public void MaxBodyLength_Ceiling_IsBase64LengthPlusLineBreaks()
    {
        // Act
        long max = ItemBody.MaxBodyLength(26_214_400);

        // Assert: ⌈26,214,400 / 3⌉ = 8,738,134 groups → 34,952,536 characters; ⌈34,952,536 / 76⌉ = 459,902 line feeds → 35,412,438
        max.Should().Be(35_412_438);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(57)]
    [InlineData(58)]
    [InlineData(1000)]
    public void TryDecode_RoundTripsRandomBytes(int length)
    {
        // Arrange
        byte[] file = new byte[length];
        new Random(length).NextBytes(file);
        Model item = Model.CreateItem(ItemEnvelopeTests.Header(), ItemEnvelopeTests.Fields(ItemRoute.Bus, driveName: null, file), file);

        // Act
        bool ok = ItemBody.TryDecode(item, out byte[]? decoded);

        // Assert
        ok.Should().BeTrue();
        decoded.Should().Equal(file);
    }
}
