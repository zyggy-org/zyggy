using Zyggy.Core.Memory;

namespace Zyggy.Core.Tests.Memory;

/// <summary>
/// Spec 58 D-Q18 / AC-9 (verify 01M81J9G3WE85TQ5H04GMA0B7Y (3)): one shared normalisation step, used by the work mail filter and by
/// the private-detail strip before any matching, removes every Unicode format character (category Cf), so an invisible character
/// can neither split a term or a number nor hide it.
/// </summary>
public sealed class FormatCharactersTests
{
    [Theory]
    [InlineData("pati­ent", "patient")]
    [InlineData("pati⁠ent", "patient")]
    [InlineData("pa​ti‌en‍t", "patient")]
    [InlineData("﻿patient", "patient")]
    [InlineData("pat‎ient‏", "patient")]
    [InlineData("‪patient‬", "patient")]
    [InlineData("⁦patient⁩", "patient")]
    [InlineData("pat؜ient", "patient")]
    [InlineData("pat\U000E0041ient", "patient")]
    [InlineData("0475­12⁠34​56", "0475123456")]
    public void Remove_FormatCharacters_Removed(string text, string expected)
    {
        // Act
        var result = FormatCharacters.Remove(text);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("patiënt, l’hôpital — 85.07.30-033.28\r\n\tok")]
    [InlineData("")]
    [InlineData("😀 emoji and ８５ full-width")]
    public void Remove_NoFormatCharacter_Unchanged(string text)
    {
        // Act
        var result = FormatCharacters.Remove(text);

        // Assert
        result.Should().Be(text);
    }

    [Theory]
    [InlineData("the pati­ent called", true)]
    [InlineData("the medisch⁠ dossier", true)]
    [InlineData("Diag​nose", true)]
    public void HasTerm_FormatCharacterInsideTerm_Found(string text, bool expected)
    {
        // Arrange
        var filter = Zyggy.Core.Outlook.WorkMailFilter.Parse("patient\ndiagnose\nmedisch dossier\n");

        // Act
        var found = filter.HasTerm(text);

        // Assert
        found.Should().Be(expected);
    }

    [Theory]
    [InlineData("85­07­30­033­28")]
    [InlineData("85.07⁠.30-033.28")]
    [InlineData("8507‎3003328")]
    public void HasNationalRegisterNumber_FormatCharacterInside_Found(string text)
    {
        // Act
        var found = Zyggy.Core.Outlook.WorkMailFilter.HasNationalRegisterNumber(text);

        // Assert
        found.Should().BeTrue();
    }

    [Theory]
    [InlineData("Call 0475­12 34 56.")]
    [InlineData("Call +32⁠475 12 34 56.")]
    [InlineData("Mail jan​@example.test.")]
    [InlineData("Lives at Kerk­straat 12.")]
    public void Strip_FormatCharacterInsideDetail_Removed(string text)
    {
        // Act
        var stripped = Zyggy.Core.Dream.PrivateDetails.Strip(text);

        // Assert
        stripped.Should().Contain(Zyggy.Core.Dream.PrivateDetails.Removed);
    }
}
