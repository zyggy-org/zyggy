using Zyggy.Core.Brief;
using Zyggy.Core.M365;
using Zyggy.Core.M365.Graph;

namespace Zyggy.Core.Tests.Brief;

/// <summary>Spec 35 AC-15..AC-19, AC-21, AC-28, AC-64..AC-66: the mail run's answer is checked before anything is written.</summary>
public sealed class MailRunValidatorTests
{
    private static string One(string id, string cls, string action, string extra = "") =>
        $$"""{"mail":[{"id":"{{id}}","class":"{{cls}}","summary":"s","action":"{{action}}"{{extra}}}],"files":[],"replies":0,"facts":0}""";

    private static (BriefDocument? Document, string? Rejection) ValidateOne(string json, PrepassMail mail, IReadOnlyDictionary<string, MessageLocation>? locations = null)
    {
        var (output, rejection) = BriefScenario.Parse(json);
        if (output is null)
        {
            return (null, rejection);
        }

        return MailRunValidator.Validate(output, new PrepassResult(BriefScenario.Watermark, [mail], []), BriefScenario.Context(10, locations), BriefScenario.Watermark);
    }

    [Theory]
    [InlineData("""{"mail":[{"id":"m01","class":"important","summary":"s","action":"z"}],"files":[],"replies":0,"facts":0}""")]
    [InlineData("""{"mail":[{"id":"m01","class":"important","summary":"s","action":"you"}],"files":[],"replies":0,"facts":0}""")]
    [InlineData("""{"mail":[{"id":"m01","class":"important","summary":"s","action":"nothing","z":{"kind":"move","destination":"archive","why":"w"},"you":{"kind":"other","action":"a","why":"w"}}],"files":[],"replies":0,"facts":0}""")]
    [InlineData("""{"mail":[{"id":"m01","class":"important","summary":"s","action":"maybe"}],"files":[],"replies":0,"facts":0}""")]
    public void Validate_TwoDecisionsOrNone_Invalid(string json)
    {
        // Act
        var (output, rejection) = BriefScenario.Parse(json);

        // Assert
        output.Should().BeNull();
        rejection.Should().Contain("m01");
    }

    [Fact]
    public void Validate_MissingClass_Invalid()
    {
        // Act
        var (output, rejection) = BriefScenario.Parse("""{"mail":[{"id":"m01","summary":"s","action":"nothing"}],"files":[],"replies":0,"facts":0}""");

        // Assert
        output.Should().BeNull();
        rejection.Should().Be("mail m01 has no class");
    }

    [Fact]
    public void Validate_OmittedMail_ImportantNotSummarisedLine()
    {
        // Act
        var document = BriefScenario.Document();

        // Assert
        var line = document.Mail.Single(m => m.Id == "m11");
        line.Class.Should().Be(MailClass.Important);
        line.Summary.Should().Be("(not summarised)");
        line.Decision.Should().Be("nothing");
    }

    [Theory]
    [InlineData("other", "nothing", ""","amount":{"status":"read","amountDue":10,"currency":"EUR","dueDate":"2026-10-10"}""", null, "Urgent")]
    [InlineData("urgent", "nothing", "", "09:00", "Important")]
    [InlineData("other", "you", ""","you":{"kind":"other","action":"call","why":"w"}""", null, "Important")]
    [InlineData("other", "z", ""","z":{"kind":"move","destination":"deleteditems","why":"w"}""", null, "Important")]
    [InlineData("other", "z", ""","z":{"kind":"move","destination":"archive","why":"w"}""", null, "Other")]
    [InlineData("other", "nothing", "", null, "Other")]
    public void Validate_Overrides_DueDateUrgent_AnsweredAtMostImportant_ActionNeverOther_OtherWithOtherActionRaised(string cls, string action, string extra, string? answered, string expected)
    {
        // Arrange
        var mail = BriefScenario.Mail("m01", "2026-10-06T06:00:00Z", "A Example", "a@example.org", "S", "c1", false, answered);
        var locations = new Dictionary<string, MessageLocation> { ["m01"] = BriefScenario.Location("m01", BriefScenario.Inbox, "c1") };

        // Act
        var (document, rejection) = ValidateOne(One("m01", cls, action, extra), mail, locations);

        // Assert
        rejection.Should().BeNull();
        document!.Mail.Single().Class.Should().Be(Enum.Parse<MailClass>(expected));
    }

    [Fact]
    public void Validate_ModelUrgent_NeverLowered()
    {
        // Arrange: a newsletter-looking mail the model calls urgent, with no facts against it
        var mail = BriefScenario.Mail("m01", "2026-10-06T06:00:00Z", "News Example", "n@example.org", "Weekly", "c1", false);

        // Act
        var (document, _) = ValidateOne(One("m01", "urgent", "nothing"), mail);

        // Assert
        document!.Mail.Single().Class.Should().Be(MailClass.Urgent);
    }

    [Fact]
    public void Validate_OtherWithArchiveMove_FoldedIntoFileOther()
    {
        // Act
        var document = BriefScenario.Document();

        // Assert
        document.OtherMailIds.Should().Equal("m05", "m06");
        var fileOther = document.Items.Single(i => i.Kind == ZKind.FileOther);
        fileOther.MessageIds.Should().Equal("m05", "m06");
        fileOther.N.Should().Be(5);
        document.Items.Should().NotContain(i => i.MessageId == "m06");
    }

    [Theory]
    [InlineData("m02", "Alice Example", true)]
    [InlineData(null, "Alice Example", false)]
    [InlineData(null, "Gus Example", true)]
    [InlineData("m05", "Alice Example", false)]
    public void Validate_File_TiedToUrgentOrImportant_OrByOther_Kept_ElseCounted(string? tiedTo, string by, bool kept)
    {
        // Arrange
        var tied = tiedTo is null ? string.Empty : $$""","tiedTo":"{{tiedTo}}" """;
        var json = $$"""{"mail":[{"id":"m02","class":"important","summary":"s","action":"nothing"},{"id":"m05","class":"other","summary":"s","action":"nothing"}],"files":[{"name":"f.docx","drive":"OneDrive","folder":"/","modified":"2026-10-06T07:00:00Z","by":"{{by}}","about":"a"{{tied}}}],"replies":0,"facts":0}""";
        var (output, _) = BriefScenario.Parse(json);
        var prepass = new PrepassResult(BriefScenario.Watermark, [BriefScenario.Prepass().Mail[1], BriefScenario.Prepass().Mail[4]], []);

        // Act
        var (document, _) = MailRunValidator.Validate(output!, prepass, BriefScenario.Context(), BriefScenario.Watermark);

        // Assert
        document!.Files.Should().HaveCount(kept ? 1 : 0);
        document.FilesOther.Should().Be(kept ? 0 : 1);
    }

    [Theory]
    [InlineData("""{"kind":"move","destination":"archive","why":"w"}""", "m01", "AQMkArchive0001", "c1", "move of m01 dropped")]
    [InlineData("""{"kind":"move","destination":"purges","why":"w"}""", "m01", "AQMkInbox0001", "c1", "move of m01 dropped")]
    [InlineData("""{"kind":"send","draftId":"d1","why":"w"}""", "d1", "AQMkInbox0001", "c1", "send for m01 dropped")]
    [InlineData("""{"kind":"send","draftId":"d1","why":"w"}""", "d1", "AQMkDrafts0001", "cOther", "send for m01 dropped")]
    [InlineData("""{"kind":"send","draftId":"dUnknown","why":"w"}""", "d1", "AQMkDrafts0001", "c1", "send for m01 dropped")]
    public void Validate_ZUnknownIdWrongFolderOrForeignConversation_DroppedCountedReason(string z, string locatedId, string folder, string conversation, string reason)
    {
        // Arrange
        var mail = BriefScenario.Mail("m01", "2026-10-06T06:00:00Z", "A Example", "a@example.org", "S", "c1", false);
        var locations = new Dictionary<string, MessageLocation> { [locatedId] = BriefScenario.Location(locatedId, folder, conversation) };

        // Act
        var (document, _) = ValidateOne(One("m01", "important", "z", ",\"z\":" + z), mail, locations);

        // Assert
        document!.Items.Should().BeEmpty();
        document.AuditReasons.Should().ContainSingle(r => r.StartsWith(reason, StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_AnsweredMailSend_Dropped()
    {
        // Act
        var document = BriefScenario.Document();

        // Assert
        document.Mail.Single(m => m.Id == "m03").Decision.Should().Be("nothing (answered 09:10)");
        document.Items.Should().NotContain(i => i.MessageId == "m03");
        document.AuditReasons.Should().Contain("reply to the answered mail m03 dropped");
    }

    [Theory]
    [InlineData("""{"status":"read","amountDue":12.5,"currency":"EUR","dueDate":"2026-10-10"}""", "you", "pay 12.50 EUR by 2026-10-10", "amount due 12.50 EUR by 2026-10-10")]
    [InlineData("""{"status":"stated","amountDue":0,"currency":"EUR"}""", "Z1", null, "amount due 0.00 EUR — nothing to pay")]
    [InlineData("""{"status":"not_read"}""", "you", "check the attachment (amount not read)", "amount not read")]
    [InlineData(null, "you", "check the attachment (amount not read)", "amount not read")]
    public void Validate_Amount_PayKeptOnlyReadOrStatedPositive(string? amount, string decision, string? youAction, string summarySuffix)
    {
        // Arrange
        var extra = ""","you":{"kind":"pay","action":"pay it","why":"due"}""" + (amount is null ? string.Empty : ",\"amount\":" + amount);
        var mail = BriefScenario.Mail("m01", "2026-10-06T06:00:00Z", "A Example", "a@example.org", "Invoice", "c1", true);
        var locations = new Dictionary<string, MessageLocation> { ["m01"] = BriefScenario.Location("m01", BriefScenario.Inbox, "c1") };

        // Act
        var (document, _) = ValidateOne(One("m01", "important", "you", extra), mail, locations);

        // Assert
        var line = document!.Mail.Single();
        line.Decision.Should().Be(decision);
        line.Summary.Should().EndWith(summarySuffix);
        if (youAction is null)
        {
            document.You.Should().BeEmpty();
            document.Items.Single().Destination.Should().Be(ZDestination.Archive);
        }
        else
        {
            document.You.Single().Action.Should().Be(youAction);
        }
    }

    [Theory]
    [InlineData("see https://example.org/pay now", "link")]
    [InlineData("mail me at bob@example.org", "address")]
    [InlineData("the key is sk-ant-api03-ABCDEFGHIJKLMNOPQRSTUVWXYZabcdef", "secret anthropic-key")]
    [InlineData("call +32 470 12 34 56", "contact detail")]
    public void Validate_TextWithUrlEmailSecretContact_WithheldReason(string summary, string reason)
    {
        // Arrange
        var json = $$"""{"mail":[{"id":"m01","class":"important","summary":"{{summary}}","action":"nothing"}],"files":[],"replies":0,"facts":0}""";
        var mail = BriefScenario.Mail("m01", "2026-10-06T06:00:00Z", "A Example", "a@example.org", "S", "c1", false);

        // Act
        var (document, _) = ValidateOne(json, mail);

        // Assert
        document!.Mail.Single().Summary.Should().Be($"[withheld: {reason}]");
        document.AuditReasons.Should().Contain($"{reason} withheld in summary of m01");
    }

    [Fact]
    public void Validate_SubjectWithAddress_Withheld()
    {
        // Arrange
        var mail = BriefScenario.Mail("m01", "2026-10-06T06:00:00Z", "A Example", "a@example.org", "FW: from carol@example.org", "c1", false);

        // Act
        var (document, _) = ValidateOne(One("m01", "important", "nothing"), mail);

        // Assert
        document!.Mail.Single().Subject.Should().Be("[withheld: address]");
        document.AuditReasons.Should().Contain("address withheld in subject");
    }

    [Theory]
    [InlineData("address withheld in sender name", false)]
    [InlineData("address withheld in subject", false)]
    [InlineData("link withheld in subject", false)]
    [InlineData("2 Z items over suggestion_cap 10 left to the owner", false)]
    [InlineData("address withheld in summary of m01", true)]
    [InlineData("link withheld in why", true)]
    [InlineData("contact detail withheld in file name", true)]
    [InlineData("move of m04 dropped: not in the Inbox or no valid destination", true)]
    [InlineData("reply to the answered mail m03 dropped", true)]
    public void IsViolation_OnlyModelTextAndModelItemsJoinTheAuditVerdict(string reason, bool violation)
    {
        // Arrange: spec 35 AC-20 — the AC-18/AC-19 reasons (the model's items and text) join the verdict; a sender name or subject taken
        // from Graph is withheld the same way but is not the model's doing (Central, 2026-10-07: a sender without a display name
        // flagged the first attended brief)

        // Act / Assert
        MailRunValidator.IsViolation(reason).Should().Be(violation);
    }

    [Fact]
    public void Validate_OverSuggestionCap_RestLeftToTheOwner()
    {
        // Arrange: three Z moves, cap 2 (one slot for the moves, one for the file-other item that always fits)
        var document = BriefScenario.Document(suggestionCap: 2);

        // Assert
        document.Items.Select(i => i.Kind).Should().Equal(ZKind.Send, ZKind.FileOther);
        document.Mail.Single(m => m.Id == "m07").Decision.Should().Be("you");
        document.Mail.Single(m => m.Id == "m08").Decision.Should().Be("you");
        document.You.Should().Contain(y => y.Subject == "Old quote" && y.Action == "move to Deleted Items");
        document.AuditReasons.Should().Contain("3 Z items over suggestion_cap 2 left to the owner");
    }
}
