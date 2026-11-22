using Zyggy.Core.Outlook;

namespace Zyggy.Core.Tests.Outlook;

// AC-4 and AC-6 (spec 58 slice 2): the pure rules of the COM access — which folder name is a well-known folder, how a folder
// path splits, and which mail is protected (its body is never asked of Outlook) — tested without Outlook.
public sealed class OutlookComRulesTests
{
    [Theory]
    [InlineData("Inbox", 6)]
    [InlineData("inbox", 6)]
    [InlineData("Drafts", 16)]
    [InlineData("Sent Items", 5)]
    [InlineData("Deleted Items", 3)]
    [InlineData("Outbox", 4)]
    [InlineData("Junk Email", 23)]
    public void WellKnownFolder_StandardName_IsTheOlDefaultFoldersValue(string name, int expected)
    {
        // Act
        var kind = OutlookComRules.WellKnownFolder(name);

        // Assert
        kind.Should().Be(expected);
    }

    [Theory]
    [InlineData("Projects")]
    [InlineData("Archive")]
    [InlineData("")]
    public void WellKnownFolder_OtherName_IsNull(string name)
    {
        // Act
        var kind = OutlookComRules.WellKnownFolder(name);

        // Assert
        kind.Should().BeNull();
    }

    [Theory]
    [InlineData("Inbox", new[] { "Inbox" })]
    [InlineData("Inbox/Projects", new[] { "Inbox", "Projects" })]
    [InlineData("Inbox\\Projects\\2026", new[] { "Inbox", "Projects", "2026" })]
    [InlineData(" Inbox / Projects ", new[] { "Inbox", "Projects" })]
    [InlineData("//", new string[0])]
    public void Segments_FolderPath_SplitsOnEitherSeparatorAndTrims(string path, string[] expected)
    {
        // Act
        var segments = OutlookComRules.Segments(path);

        // Assert
        segments.Should().Equal(expected);
    }

    [Theory]
    [InlineData("IPM.Note.SMIME", new string[0], true)]
    [InlineData("IPM.Note.SMIME.MultipartSigned", new string[0], false)]
    [InlineData("ipm.note.smime", new string[0], true)]
    [InlineData("IPM.Note", new[] { "message.rpmsg" }, true)]
    [InlineData("IPM.Note", new[] { "Message.RPMSG" }, true)]
    [InlineData("IPM.Note", new[] { "report.pdf" }, false)]
    [InlineData("IPM.Note", new string[0], false)]
    [InlineData("", new string[0], false)]
    public void IsProtected_EncryptedOrRightsManaged_True_SignedOrPlain_False(string messageClass, string[] attachments, bool expected)
    {
        // Act
        var isProtected = OutlookComRules.IsProtected(messageClass, attachments);

        // Assert
        isProtected.Should().Be(expected);
    }
}
