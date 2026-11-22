using Zyggy.Core.Dream;

namespace Zyggy.Core.Tests.Dream;

/// <summary>
/// Spec 58 D-Q18 (re-verify 01M81H6RBP1PCG45ZN4BZ2DSRA (4)): before the dream archives a work mail line, third parties' phone numbers,
/// e-mail addresses and postal addresses are removed; name, role and organisation stay. Best-effort, as the filter.
/// </summary>
public sealed class PrivateDetailsTests
{
    [Theory]
    [InlineData("Call Jan Peeters (project lead, Acme) on 0475 12 34 56 today.", "Call Jan Peeters (project lead, Acme) on [private detail removed] today.")]
    [InlineData("Mobile +32 475 12 34 56.", "Mobile [private detail removed].")]
    [InlineData("Mobile +32 (0)475/12.34.56.", "Mobile [private detail removed].")]
    [InlineData("Tel 02/123.45.67 or 0032 2 123 45 67.", "Tel [private detail removed] or [private detail removed].")]
    [InlineData("Tel 0475123456.", "Tel [private detail removed].")]
    [InlineData("Tel 0475–12–34–56.", "Tel [private detail removed].")]
    [InlineData("Numbers 0475 12 34 56 0476 98 76 54.", "Numbers [private detail removed].")]
    [InlineData("Write to jan.peeters@example.test.", "Write to [private detail removed].")]
    [InlineData("Write to j.p+mail@sub.example.test now.", "Write to [private detail removed] now.")]
    [InlineData("He lives at Kerkstraat 12, 2000 Antwerpen.", "He lives at [private detail removed].")]
    [InlineData("She lives at Grote Steenweg 120 bus 3.", "She lives at Grote [private detail removed].")]
    [InlineData("Lives rue de la Loi 16, 1000 Bruxelles, alone.", "Lives [private detail removed], alone.")]
    [InlineData("Lives at 12 Baker Street now.", "Lives at [private detail removed] now.")]
    public void Strip_ContactDetails_Removed(string text, string expected)
    {
        // Act
        var stripped = PrivateDetails.Strip(text);

        // Assert
        stripped.Should().Be(expected);
    }

    [Theory]
    [InlineData("Jan Peeters, project lead at Acme, asks to confirm the Q4 budget by Friday.")]
    [InlineData("The meeting of 01/02/2026 at 09:00 moved to 2026-11-21.")]
    [InlineData("Total 1 234,00 EUR for 12 licences, PO 4500123456.")]
    [InlineData("Invoice 2026-001234 is due; ticket INC0012345 is closed.")]
    [InlineData("The Q4 budget for the Kerkstraat project is under review.")]
    public void Strip_NameRoleOrganisationAndOrdinaryFigures_Kept(string text)
    {
        // Act
        var stripped = PrivateDetails.Strip(text);

        // Assert
        stripped.Should().Be(text);
    }
}
