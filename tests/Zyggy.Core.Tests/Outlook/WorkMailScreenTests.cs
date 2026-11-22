using Zyggy.Core.Outlook;

namespace Zyggy.Core.Tests.Outlook;

/// <summary>
/// AC-9 (spec 58 slice 5): the source screen of a job run judges a mail before any of it is printed — subject, sender name and address,
/// whole body and attachment names; a register number or a term keeps it, a protected mail (body never read) is in doubt; an ordinary
/// mail passes. <see cref="WorkMailFilter.IsSensitiveText"/> judges a report text the same way.
/// </summary>
public sealed class WorkMailScreenTests
{
    private static readonly WorkMailFilter Filter = WorkMailFilter.Parse("patient\nmedisch dossier\n");

    [Theory]
    [InlineData("Budget", "Can you confirm the budget?", null, "Share")]
    [InlineData("Patients moved", "See below.", null, "KeepSensitive")]
    [InlineData("Registration", "Please register 85.07.30-033.28.", null, "KeepSensitive")]
    [InlineData("Report", "See attached.", "Medisch-dossier.pdf", "KeepSensitive")]
    public void Screen_Mail_Verdict(string subject, string body, string? attachment, string expected)
    {
        // Arrange
        var mail = Mail(subject, body, attachment is null ? [] : [attachment]);

        // Act
        var verdict = Filter.Screen(mail);

        // Assert
        verdict.ToString().Should().Be(expected);
    }

    [Fact]
    public void Screen_TermInTheSenderName_KeepSensitive()
    {
        // Arrange
        var mail = Mail("Hello", "Hi.", []) with { Summary = Mail("Hello", "Hi.", []).Summary with { SenderName = "Patient services" } };

        // Act
        var verdict = Filter.Screen(mail);

        // Assert
        verdict.Should().Be(WorkMailVerdict.KeepSensitive);
    }

    [Fact]
    public void Screen_ProtectedMail_KeepInDoubt()
    {
        // Arrange
        var mail = Mail("Sealed", null, []);

        // Act
        var verdict = Filter.Screen(mail);

        // Assert
        verdict.Should().Be(WorkMailVerdict.KeepInDoubt);
    }

    [Theory]
    [InlineData("Urgent: the Q4 budget.", false)]
    [InlineData("the patients of ward 3", true)]
    [InlineData("85 07 30 033 28", true)]
    public void IsSensitiveText_Report(string text, bool expected)
    {
        // Act
        bool sensitive = Filter.IsSensitiveText(text);

        // Assert
        sensitive.Should().Be(expected);
    }

    private static OutlookMail Mail(string subject, string? body, string[] attachments) =>
        new(
            new OutlookMailSummary("r1", "Sender", "sender@example.test", subject, "2026-11-21T09:00:00+01:00", "normal", "none", false, attachments.Length > 0, body is null),
            attachments,
            body,
            false);
}
