using Zyggy.Core.Memory;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Memory;

/// <summary>
/// Spec 35 AC-30: a line where only number-shaped patterns (<c>iban</c>, <c>card-number</c>) match keeps its other text with the
/// number replaced by <c>[redacted: &lt;name&gt;]</c>; the result is re-tested against every pattern and refused if anything still matches.
/// </summary>
public sealed class SecretPatternsRedactionTests
{
    private static SecretPatterns Patterns() => SecretPatterns.Load(Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt")).Patterns!;

    public static TheoryData<string, string> SecretSamples() => SecretPatternsTests.SecretSamples();

    [Theory]
    [InlineData("Direct debit from IBAN BE71 0961 2345 6769 monthly", "Direct debit from IBAN [redacted: iban] monthly")]
    [InlineData("IBAN BE71096123456769.", "IBAN [redacted: iban].")]
    [InlineData("pay to BE71 0961 2345 6769 before Friday", "pay to [redacted: iban] before Friday")]
    public void Redact_IbanSpacedOrJoined_TokenReplacedRestKept(string line, string expected)
    {
        // Act
        var ok = Patterns().TryRedactNumberShaped(line, out var redacted, out var names);

        // Assert
        ok.Should().BeTrue();
        redacted.Should().Be(expected);
        names.Should().Equal("iban");
    }

    [Theory]
    [InlineData("Card on file: 4111-1111-1111-1111 (expires 12/28)", "Card on file: [redacted: card-number] (expires 12/28)")]
    [InlineData("card 4111 1111 1111 1111", "card [redacted: card-number]")]
    public void Redact_CardNumber_Replaced(string line, string expected)
    {
        // Act
        var ok = Patterns().TryRedactNumberShaped(line, out var redacted, out var names);

        // Assert
        ok.Should().BeTrue();
        redacted.Should().Be(expected);
        names.Should().Equal("card-number");
    }

    [Fact]
    public void Redact_IbanAndCardOnOneLine_BothReplacedInOrder()
    {
        // Act
        var ok = Patterns().TryRedactNumberShaped("IBAN BE71 0961 2345 6769 or card 4111 1111 1111 1111", out var redacted, out var names);

        // Assert
        ok.Should().BeTrue();
        redacted.Should().Be("IBAN [redacted: iban] or card [redacted: card-number]");
        names.Should().Equal("iban", "card-number");
    }

    [Theory]
    [InlineData("Refund to IBAN BE71 0961 2345 6769, reference token=abcdef123456")]
    [InlineData("BE71 0961 2345 6769 AKIAABCDEFGHIJKLMNOP")]
    public void Redact_LineWithIbanAndOtherSecret_False(string line)
    {
        // Act
        var ok = Patterns().TryRedactNumberShaped(line, out _, out _);

        // Assert
        ok.Should().BeFalse("the line still matches another pattern after the number is gone: withheld whole");
    }

    [Theory]
    [InlineData("password: hunter2x")]
    [InlineData("AKIAABCDEFGHIJKLMNOP")]
    [InlineData("nothing secret here, invoice 2026-41 due 2026-10-10")]
    public void Redact_NoNumberShapedMatch_False(string line)
    {
        // Act
        var ok = Patterns().TryRedactNumberShaped(line, out _, out _);

        // Assert
        ok.Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(SecretSamples))]
    public void Redact_EverySecretSample_ResultNeverMatchesAnyPattern(string name, string sample)
    {
        // Act
        var ok = Patterns().TryRedactNumberShaped(sample, out var redacted, out _);

        // Assert: the invariant — whatever is printed matches no pattern
        if (ok)
        {
            Patterns().TryMatch(redacted, out var still).Should().BeFalse($"{name}: '{redacted}' still matches {still}");
            redacted.Should().Contain("[redacted: ");
        }
        else
        {
            name.Should().NotBe("card-number", "a bare card number is always redactable");
        }
    }
}
