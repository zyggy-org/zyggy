using Zyggy.Core.Memory;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Memory;

/// <summary>The <c>facts.sh</c> validator (spec 33 AC-12): refusal reasons in the script's order, its phone rule, the 240-character cut.</summary>
public sealed class FactValidatorTests
{
    private static readonly SecretPatterns Patterns =
        SecretPatterns.Load(Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt")).Patterns!;

    [Theory]
    [InlineData("", "empty")]
    [InlineData("- starts with dash", "non-letter start")]
    [InlineData("2026 was a good year", "non-letter start")]
    [InlineData("🎉🎉🎉", "non-letter start")]
    [InlineData("Mail carol@example.org about the invoice", "e-mail address")]
    [InlineData("See https://example.org/x", "url")]
    [InlineData("See HTTPS://example.org/x", "url")]
    [InlineData("Details are on www.example.org for the whole team.", "url")]
    [InlineData("Details are on WWW.example.org", "url")]
    [InlineData("Call +32 470 12 34 56", "phone")]
    [InlineData("Token ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789", "secret pattern github-token")]
    [InlineData("Pay the supplier on BE71 0961 2345 6769 before Friday.", "secret pattern iban")]
    [InlineData("Order 4111111111111111 was shipped to the warehouse.", "secret pattern card-number")]
    public void Refusal_Line_NamesReasonInScriptOrder(string fact, string reason)
    {
        // Act
        var refusal = FactValidator.Refusal(fact, Patterns);

        // Assert
        refusal.Should().Be(reason);
    }

    [Theory]
    [InlineData("Alice promised Example Org the Q4 integration plan by mid-October.")]
    [InlineData("Émile chairs the meeting.")]
    [InlineData("The meeting moved to 01.10.2026 at the office.")]
    [InlineData("Invoice 2026-41 is due.")]
    public void Refusal_AcceptableLine_Null(string fact)
    {
        // Assert
        FactValidator.Refusal(fact, Patterns).Should().BeNull();
    }

    [Fact]
    public void Phone_Date01102026_Passes()
    {
        // Assert: "0" then 7 more digits is 8 digits, a phone needs 9 after a leading 0
        FactValidator.Refusal("Due 01.10.2026 latest", Patterns).Should().BeNull();
    }

    [Fact]
    public void Phone_Plus8Digits_Refused()
    {
        // Assert
        FactValidator.Refusal("Reach him on +3247012 3 for that", Patterns).Should().Be("phone");
    }

    [Fact]
    public void Phone_Plus7Digits_Passes()
    {
        // Assert
        FactValidator.Refusal("Rated +1234567 points", Patterns).Should().BeNull();
    }

    [Fact]
    public void Phone_Leading0Nine_Refused()
    {
        // Assert
        FactValidator.Refusal("Desk 0470 12 34 5 is free", Patterns).Should().Be("phone");
    }

    [Fact]
    public void Phone_ZeroInsideAWord_NotACandidate()
    {
        // Assert: the 0 must follow a non-alphanumeric character or start the line
        FactValidator.Refusal("Ticket A0470123456 was closed", Patterns).Should().BeNull();
    }

    [Fact]
    public void Clean_ControlCharactersRemovedTabsCollapsed()
    {
        // Act
        var cleaned = FactValidator.Clean("Alice\tleads the\a weekly\r review.\u007f");

        // Assert
        cleaned.Should().Be("Alice leads the weekly review.");
    }

    [Fact]
    public void Cut_241Characters_239PlusEllipsis()
    {
        // Arrange
        var fact = "é" + new string('a', 240);

        // Act
        var cut = FactValidator.Cut(fact);

        // Assert
        cut.Should().Be("é" + new string('a', 238) + "…");
        TextCollapse.CharCount(cut).Should().Be(240);
    }

    [Fact]
    public void Cut_EmojiCountedAsOneCharacter()
    {
        // Arrange
        var fact = string.Concat(Enumerable.Repeat("😀", 241));

        // Act
        var cut = FactValidator.Cut(fact);

        // Assert
        TextCollapse.CharCount(cut).Should().Be(240);
        cut.Should().EndWith("😀…");
    }
}
