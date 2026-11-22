using Zyggy.Core.Outlook;

namespace Zyggy.Core.Tests.Outlook;

/// <summary>
/// AC-9 (spec 58 slice 4): the sensitive-data filter on the work laptop. A mail that holds a national register number with a valid
/// check digit, or a term of the shared sensitive list, or that the session marks in doubt, yields nothing for Central; so does a mail
/// whose body could not be read (protected) or whose facts name an attachment. Best-effort, as the owner accepted.
/// </summary>
public sealed class WorkMailFilterTests
{
    private static readonly WorkMailFilter Filter = WorkMailFilter.Parse("# shared sensitive terms\npatient\n\n  Diagnose  \nmedisch dossier\n");

    // ---- national register numbers ----

    [Theory]
    [InlineData("85.07.30-033.28", true)]
    [InlineData("85073003328", true)]
    [InlineData("850730 033 28", true)]
    [InlineData("NN 85.07.30-033.28 on file", true)]
    [InlineData("01.02.03-123.45", true)]
    [InlineData("85.07.30-033.29", false)]
    [InlineData("12345678901", false)]
    [InlineData("0475 12 34 56", false)]
    [InlineData("850730033281", false)]
    [InlineData("invoice 2026-11-22", false)]
    public void HasNationalRegisterNumber_ValidCheckDigitOnly(string text, bool expected)
    {
        // Act
        var found = WorkMailFilter.HasNationalRegisterNumber(text);

        // Assert
        found.Should().Be(expected);
    }

    // ---- the term list ----

    [Theory]
    [InlineData("The patient called back.", true)]
    [InlineData("PATIENT intake moved", true)]
    [InlineData("see the diagnose of Monday", true)]
    [InlineData("the medisch dossier is ready", true)]
    [InlineData("outpatients are elsewhere", false)]
    [InlineData("the budget review", false)]
    public void HasTerm_WholeWordsCaseInsensitive(string text, bool expected)
    {
        // Act
        var found = Filter.HasTerm(text);

        // Assert
        found.Should().Be(expected);
    }

    [Fact]
    public void Parse_CommentsAndBlankLinesIgnored_TermsTrimmed()
    {
        // Act
        var terms = Filter.Terms;

        // Assert
        terms.Should().Equal("patient", "Diagnose", "medisch dossier");
    }

    // ---- one mail ----

    [Fact]
    public void Check_PlainBusinessMail_Shared()
    {
        // Act
        var verdict = Filter.Check(Mail(body: "Can you confirm the budget?"), ["Asks to confirm the Q4 budget"], doubt: false);

        // Assert
        verdict.Should().Be(WorkMailVerdict.Share);
    }

    [Theory]
    [InlineData("Re: 85.07.30-033.28", "Hello", "Asks for a call")]
    [InlineData("Budget", "His number is 85073003328.", "Asks for a call")]
    [InlineData("Budget", "Hello", "Mentions 85.07.30-033.28")]
    [InlineData("Patient file", "Hello", "Asks for a call")]
    [InlineData("Budget", "the diagnose is attached", "Asks for a call")]
    [InlineData("Budget", "Hello", "About a patient")]
    public void Check_NeverListAnywhere_KeptAsSensitive(string subject, string body, string fact)
    {
        // Act
        var verdict = Filter.Check(Mail(subject: subject, body: body), [fact], doubt: false);

        // Assert
        verdict.Should().Be(WorkMailVerdict.KeepSensitive);
    }

    [Fact]
    public void Check_SessionMarksDoubt_KeptInDoubt()
    {
        // Act
        var verdict = Filter.Check(Mail(body: "Hello"), ["Asks for a call"], doubt: true);

        // Assert
        verdict.Should().Be(WorkMailVerdict.KeepInDoubt);
    }

    [Fact]
    public void Check_ProtectedMail_BodyUnread_KeptInDoubt()
    {
        // Act
        var verdict = Filter.Check(Mail(body: null), ["Asks for a call"], doubt: false);

        // Assert
        verdict.Should().Be(WorkMailVerdict.KeepInDoubt);
    }

    [Fact]
    public void Check_FactNamesAnAttachment_KeptInDoubt()
    {
        // Act
        var verdict = Filter.Check(Mail(body: "See attached.", attachments: ["Offer-2026.pdf"]), ["Sends offer-2026.pdf for review"], doubt: false);

        // Assert
        verdict.Should().Be(WorkMailVerdict.KeepInDoubt);
    }

    [Fact]
    public void Check_NoFacts_KeptInDoubt()
    {
        // Act
        var verdict = Filter.Check(Mail(body: "Hello"), [], doubt: false);

        // Assert
        verdict.Should().Be(WorkMailVerdict.KeepInDoubt);
    }

    private static OutlookMail Mail(string subject = "Budget", string? body = "Hello", string[]? attachments = null) =>
        new(
            new OutlookMailSummary("m1", "Sender m1", "m1@example.test", subject, "2026-11-21T09:00:00+01:00", "normal", "none", false, (attachments ?? []).Length > 0, body is null),
            attachments ?? [],
            body,
            false);
}
