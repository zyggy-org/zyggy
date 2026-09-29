using System.Text;

using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Envelope;

public sealed class EnvelopeWriterTests
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    [Theory]
    [InlineData("job")]
    [InlineData("report")]
    [InlineData("context")]
    [InlineData("context-crlf-body")]
    public void Serialize_CanonicalStyleGoldenCase_RoundTripsByteIdentically(string name)
    {
        // Arrange
        byte[] md = Golden.Md(name);

        // Act
        byte[] written = EnvelopeWriter.Serialize(Parse(md));

        // Assert
        written.Should().Equal(md);
    }

    [Fact]
    public void Serialize_GoldenJobNoncanonical_ReparsesAndCanonicalisesToGoldenAndKeepsUnknownKeys()
    {
        // Act
        byte[] written = EnvelopeWriter.Serialize(Parse(Golden.Md("job-noncanonical")));

        // Assert
        Model reparsed = Parse(written);
        EnvelopeSigner.Canonicalize(reparsed).Should().Equal(Golden.Canonical("job-noncanonical"));
        string text = Encoding.UTF8.GetString(written);
        text.Should().Contain("\nx_experimental: 'a: b'\n").And.Contain("\nx_meta:\n");
        text.Should().NotContain("\r");
    }

    [Fact]
    public void Serialize_UnsignedEnvelope_ThrowsInvalidOperationException()
    {
        // Arrange
        Model unsigned = Model.CreateJob(EnvelopeFactoryTests.Header(), new JobFields("calizr"), "body\n"u8.ToArray());
        Action act = () => EnvelopeWriter.Serialize(unsigned);

        // Act & Assert
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Serialize_Null_ThrowsArgumentNullException()
    {
        // Arrange
        Action act = () => EnvelopeWriter.Serialize(null!);

        // Act & Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Serialize_Output_StartsWithDelimiterAndHasNoBom()
    {
        // Act
        byte[] written = EnvelopeWriter.Serialize(Parse(Golden.Md("job").Prepend((byte)0xBF).Prepend((byte)0xBB).Prepend((byte)0xEF).ToArray()));

        // Assert
        written.AsSpan().StartsWith("---\n"u8).Should().BeTrue();
    }

    private static Model Parse(byte[] file)
    {
        EnvelopeResult result = EnvelopeParser.Parse(file, Acme);
        result.IsAccepted.Should().BeTrue(result.Rejection?.ToString());
        return result.Envelope!;
    }
}
