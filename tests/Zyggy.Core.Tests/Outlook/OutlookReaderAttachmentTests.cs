using static Zyggy.Core.Tests.Outlook.OutlookTestData;

namespace Zyggy.Core.Tests.Outlook;

// AC-4: attachments are never opened or saved (the access interface has no member that could); only their names and count
// are shown. A protected mail is listed as protected without its body, and its body is never asked of Outlook.
public sealed class OutlookReaderAttachmentTests
{
    private static readonly string[] TwoNames = ["plan.pdf", "budget.xlsx"];

    [Fact]
    public void Read_MailWithAttachments_ShowsOnlyTheirNamesAndCount()
    {
        // Arrange
        var access = new FakeOutlookAccess().Add(
            FakeOutlookAccess.DefaultStore, "Inbox", FakeOutlookAccess.Item("a1", Local(2026, 11, 21, 9), attachments: ["plan.pdf", "budget.xlsx"]), body: "See attached.");
        var sut = Reader(access);

        // Act
        var mail = sut.Read("a1").Value!;

        // Assert
        mail.Should().BeEquivalentTo(new { AttachmentNames = TwoNames, AttachmentCount = 2, Body = "See attached." });
    }

    [Fact]
    public void Read_ProtectedMail_HasNoBody()
    {
        // Arrange
        var access = new FakeOutlookAccess().Add(
            FakeOutlookAccess.DefaultStore, "Inbox", FakeOutlookAccess.Item("p1", Local(2026, 11, 21, 9), isProtected: true), body: "SECRET-CANARY");
        var sut = Reader(access);

        // Act
        var mail = sut.Read("p1").Value!;

        // Assert
        mail.Should().BeEquivalentTo(new { Body = (string?)null, Summary = new { IsProtected = true } });
    }

    [Fact]
    public void Read_ProtectedMail_NeverAsksOutlookForTheBody()
    {
        // Arrange
        var access = new FakeOutlookAccess().Add(
            FakeOutlookAccess.DefaultStore, "Inbox", FakeOutlookAccess.Item("p1", Local(2026, 11, 21, 9), isProtected: true), body: "SECRET-CANARY");
        var sut = Reader(access);

        // Act
        sut.Read("p1");

        // Assert
        access.Calls.Should().NotContain(c => c.StartsWith("ReadBody", StringComparison.Ordinal));
    }

    [Fact]
    public void List_ProtectedMail_IsListedAsProtected()
    {
        // Arrange
        var access = new FakeOutlookAccess().Add(FakeOutlookAccess.DefaultStore, "Inbox", FakeOutlookAccess.Item("p1", Local(2026, 11, 21, 9), isProtected: true));
        var sut = Reader(access);

        // Act
        var mail = sut.List("Inbox", Since(2026, 11, 1)).Value!.Single();

        // Assert
        mail.IsProtected.Should().BeTrue();
    }

    [Fact]
    public void List_AnyMail_NeverReadsABody()
    {
        // Arrange
        var access = new FakeOutlookAccess().Add(FakeOutlookAccess.DefaultStore, "Inbox", FakeOutlookAccess.Item("m1", Local(2026, 11, 21, 9), attachments: ["x.pdf"]), body: "body");
        var sut = Reader(access);

        // Act
        sut.List("Inbox", Since(2026, 11, 1));

        // Assert
        access.Calls.Should().NotContain(c => c.StartsWith("ReadBody", StringComparison.Ordinal));
    }
}
