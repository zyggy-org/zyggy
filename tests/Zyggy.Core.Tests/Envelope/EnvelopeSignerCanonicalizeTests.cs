using System.Text;

using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Envelope;

public sealed class EnvelopeSignerCanonicalizeTests
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    [Theory]
    [MemberData(nameof(Golden.Cases), MemberType = typeof(Golden))]
    public void Canonicalize_EveryGoldenCase_EqualsCanonicalBytes(string name)
    {
        // Arrange
        Model envelope = Parse(Golden.Md(name));
        byte[] expected = Golden.Canonical(name);

        // Act
        byte[] canonical = EnvelopeSigner.Canonicalize(envelope);

        // Assert
        canonical.Should().Equal(expected, Explain(expected, canonical));
    }

    [Fact]
    public void Canonicalize_GoldenJob_IsFileMinusFirstLineAndSigLine()
    {
        // Arrange
        byte[] md = Golden.Md("job");
        List<byte[]> lines = SplitKeepingNewlines(md);
        byte[] expected = [.. lines.Skip(1).Where(l => !l.AsSpan().StartsWith("sig: "u8)).SelectMany(l => l)];

        // Act
        byte[] canonical = EnvelopeSigner.Canonicalize(Parse(md));

        // Assert
        canonical.Should().Equal(expected);
    }

    [Fact]
    public void Canonicalize_UnknownKeySortingBeforeAgent_IsEmittedFirst()
    {
        // Arrange
        Model envelope = Parse(EnvelopeText.From(Golden.Md("job")).WithKey("_x", "1").ToBytes());

        // Act
        string canonical = Text(EnvelopeSigner.Canonicalize(envelope));

        // Assert
        canonical.Should().StartWith("_x: 1\nagent: env-debugger\n");
    }

    [Fact]
    public void Canonicalize_EmptySequence_EmitsEmptyFlowBrackets()
    {
        // Arrange
        Model envelope = Parse(EnvelopeText.From(Golden.Md("job")).WithValue("allowed_tools", "[]").ToBytes());

        // Act
        string canonical = Text(EnvelopeSigner.Canonicalize(envelope));

        // Assert
        canonical.Should().Contain("\nallowed_tools: []\n");
    }

    [Fact]
    public void Canonicalize_SequenceWithMappingItem_EmitsBlockStyleIndented()
    {
        // Arrange
        byte[] file = EnvelopeText.From(Golden.Md("job"))
            .WithLine("x_steps:").WithLine("- name: one").WithLine("- name: two").ToBytes();

        // Act
        string canonical = Text(EnvelopeSigner.Canonicalize(Parse(file)));

        // Assert
        canonical.Should().Contain("\nx_steps:\n  - name: one\n  - name: two\n---\n");
    }

    [Theory]
    [InlineData("'a: b'", "'a: b'")]
    [InlineData("'#x'", "'#x'")]
    [InlineData("' lead'", "' lead'")]
    [InlineData("'-x'", "-x")]
    [InlineData("'- x'", "'- x'")]
    [InlineData("it's", "it's")]
    [InlineData("\"it's: here\"", "\"it's: here\"")]
    [InlineData("\"line1\\nline2\"", "\"line1\\nline2\"")]
    [InlineData("\"it's\\r\\n\"", "\"it's\\r\\n\"")]
    [InlineData("''", "''")]
    [InlineData("\"plain\"", "plain")]
    public void Canonicalize_ScalarNeedingQuotes_UsesSingleThenDoubleQuotes(string yamlValue, string emitted)
    {
        // Arrange
        Model envelope = Parse(EnvelopeText.From(Golden.Md("job")).WithKey("x_value", yamlValue).ToBytes());

        // Act
        string canonical = Text(EnvelopeSigner.Canonicalize(envelope));

        // Assert
        canonical.Should().Contain("\nx_value: " + emitted + "\n");
    }

    [Fact]
    public void Canonicalize_SigPresent_IsNotEmitted()
    {
        // Act
        string canonical = Text(EnvelopeSigner.Canonicalize(Parse(Golden.Md("job"))));

        // Assert
        canonical.Should().NotContain("sig:");
        canonical.Should().Contain("\nkey_id: acme/1\n");
    }

    [Fact]
    public void Canonicalize_CrlfFrontMatter_EmitsLf()
    {
        // Act
        string canonical = Text(EnvelopeSigner.Canonicalize(Parse(Golden.Md("job-noncanonical"))));

        // Assert
        canonical[..canonical.IndexOf("\n---\n", StringComparison.Ordinal)].Should().NotContain("\r");
    }

    [Fact]
    public void Canonicalize_Null_ThrowsArgumentNullException()
    {
        // Arrange
        Action act = () => EnvelopeSigner.Canonicalize(null!);

        // Act & Assert
        act.Should().Throw<ArgumentNullException>();
    }

    private static Model Parse(byte[] file)
    {
        EnvelopeResult result = EnvelopeParser.Parse(file, Acme);
        result.IsAccepted.Should().BeTrue(result.Rejection?.ToString());
        return result.Envelope!;
    }

    private static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    private static List<byte[]> SplitKeepingNewlines(byte[] bytes)
    {
        var lines = new List<byte[]>();
        int start = 0;
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\n')
            {
                lines.Add(bytes[start..(i + 1)]);
                start = i + 1;
            }
        }

        if (start < bytes.Length)
        {
            lines.Add(bytes[start..]);
        }

        return lines;
    }

    private static string Explain(byte[] expected, byte[] actual)
    {
        int offset = 0;
        while (offset < expected.Length && offset < actual.Length && expected[offset] == actual[offset])
        {
            offset++;
        }

        static string Escape(byte[] b) => Encoding.UTF8.GetString(b).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n\n", StringComparison.Ordinal);
        return $"first difference at byte {offset}\n--- expected ---\n{Escape(expected)}\n--- actual ---\n{Escape(actual)}";
    }
}
