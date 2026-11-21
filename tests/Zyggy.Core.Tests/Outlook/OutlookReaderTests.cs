using Zyggy.Core.Outlook;

using static Zyggy.Core.Tests.Outlook.OutlookTestData;

namespace Zyggy.Core.Tests.Outlook;

// AC-2: given a fake Outlook with mail in two stores, the read path lists only default-store Inbox items since a date, newest
// first, capped, with the listed fields and ISO 8601 dates, and reads one item's plain-text body cut at the configured length.
public sealed class OutlookReaderTests
{
    private static FakeOutlookAccess TwoStores() =>
        new FakeOutlookAccess()
            .Add(FakeOutlookAccess.DefaultStore, "Inbox", FakeOutlookAccess.Item("a-old", Local(2026, 11, 19, 9)))
            .Add(FakeOutlookAccess.DefaultStore, "Inbox", FakeOutlookAccess.Item("b-mid", Local(2026, 11, 20, 14, 30)))
            .Add(FakeOutlookAccess.DefaultStore, "Inbox", FakeOutlookAccess.Item("c-new", Local(2026, 11, 21, 8, 5)))
            .Add(FakeOutlookAccess.SharedStore, "Inbox", FakeOutlookAccess.Item("s-shared", Local(2026, 11, 21, 10)));

    [Fact]
    public void List_TwoStores_ReturnsDefaultStoreInboxSinceDateNewestFirst()
    {
        // Arrange
        var sut = Reader(TwoStores());

        // Act
        var result = sut.List("Inbox", Since(2026, 11, 20));

        // Assert
        result.Value!.Select(m => m.Reference).Should().Equal("c-new", "b-mid");
    }

    [Fact]
    public void List_TwoStores_NeverAsksTheSharedStore()
    {
        // Arrange
        var access = TwoStores();
        var sut = Reader(access);

        // Act
        sut.List("Inbox", Since(2026, 11, 1));

        // Assert
        access.Calls.Should().NotContain(c => c.Contains(FakeOutlookAccess.SharedStore, StringComparison.Ordinal));
    }

    [Fact]
    public void List_MoreThanMaxItems_IsCappedAtTheConfiguredMaximum()
    {
        // Arrange
        var sut = Reader(TwoStores(), settings: Settings(maxItems: 2));

        // Act
        var result = sut.List("Inbox", Since(2026, 11, 1), max: 10);

        // Assert
        result.Value!.Select(m => m.Reference).Should().Equal("c-new", "b-mid");
    }

    [Fact]
    public void List_CallerAsksFewerThanTheMaximum_ReturnsTheCallersCount()
    {
        // Arrange
        var sut = Reader(TwoStores());

        // Act
        var result = sut.List("Inbox", Since(2026, 11, 1), max: 1);

        // Assert
        result.Value!.Select(m => m.Reference).Should().Equal("c-new");
    }

    [Fact]
    public void List_OneItem_CarriesTheListedFieldsWithAnIsoDate()
    {
        // Arrange
        var access = new FakeOutlookAccess().Add(
            FakeOutlookAccess.DefaultStore,
            "Inbox",
            FakeOutlookAccess.Item("x1", Local(2026, 11, 21, 8, 5), subject: "Budget", importance: 2, flag: 2, unread: false, attachments: ["plan.pdf"]));
        var sut = Reader(access);

        // Act
        var result = sut.List("Inbox", Since(2026, 11, 1));

        // Assert
        result.Value.Should().Equal(new OutlookMailSummary(
            "x1", "Sender x1", "x1@example.test", "Budget", "2026-11-21T08:05:00+01:00", "high", "flagged", IsRead: true, HasAttachments: true, IsProtected: false));
    }

    [Theory]
    [InlineData(0, 0, "low", "none")]
    [InlineData(1, 1, "normal", "complete")]
    [InlineData(2, 2, "high", "flagged")]
    [InlineData(7, 9, "normal", "none")]
    public void List_ImportanceAndFlag_AreWrittenAsWords(int importance, int flag, string expectedImportance, string expectedFlag)
    {
        // Arrange
        var access = new FakeOutlookAccess().Add(FakeOutlookAccess.DefaultStore, "Inbox", FakeOutlookAccess.Item("x1", Local(2026, 11, 21, 8), importance: importance, flag: flag));
        var sut = Reader(access);

        // Act
        var mail = sut.List("Inbox", Since(2026, 11, 1)).Value!.Single();

        // Assert
        (mail.Importance, mail.Flag).Should().Be((expectedImportance, expectedFlag));
    }

    [Fact]
    public void List_NamedFolderOfTheDefaultStore_IsListed()
    {
        // Arrange
        var access = TwoStores().Add(FakeOutlookAccess.DefaultStore, "Inbox/Projects", FakeOutlookAccess.Item("p1", Local(2026, 11, 21, 9)));
        var sut = Reader(access);

        // Act
        var result = sut.List("Inbox/Projects", Since(2026, 11, 1));

        // Assert
        result.Value!.Select(m => m.Reference).Should().Equal("p1");
    }

    [Fact]
    public void List_FolderOnlyInTheSharedStore_IsRefusedFolderNotFound()
    {
        // Arrange
        var access = TwoStores().AddFolder(FakeOutlookAccess.SharedStore, "Shared only");
        var sut = Reader(access);

        // Act
        var result = sut.List("Shared only", Since(2026, 11, 1));

        // Assert
        result.Refusal.Should().Be(OutlookRefusal.FolderNotFound);
    }

    [Fact]
    public void Read_DefaultStoreItem_ReturnsThePlainTextBody()
    {
        // Arrange
        var access = TwoStores().Add(FakeOutlookAccess.DefaultStore, "Inbox", FakeOutlookAccess.Item("r1", Local(2026, 11, 21, 9)), body: "Hello,\nplease review.");
        var sut = Reader(access);

        // Act
        var result = sut.Read("r1");

        // Assert
        result.Value.Should().BeEquivalentTo(new { Body = "Hello,\nplease review.", BodyTruncated = false });
    }

    [Fact]
    public void Read_LongBody_IsCutAtTheConfiguredLength()
    {
        // Arrange
        var access = new FakeOutlookAccess().Add(FakeOutlookAccess.DefaultStore, "Inbox", FakeOutlookAccess.Item("r1", Local(2026, 11, 21, 9)), body: "0123456789ABCDEF");
        var sut = Reader(access, settings: Settings(bodyMaxChars: 10));

        // Act
        var result = sut.Read("r1");

        // Assert
        result.Value.Should().BeEquivalentTo(new { Body = "0123456789", BodyTruncated = true });
    }

    [Fact]
    public void Read_ItemOfTheSharedStore_IsRefusedItemNotFound()
    {
        // Arrange
        var sut = Reader(TwoStores());

        // Act
        var result = sut.Read("s-shared");

        // Assert
        result.Refusal.Should().Be(OutlookRefusal.ItemNotFound);
    }

    [Fact]
    public void Count_InboxDraftsAndANamedFolder_AreCountedInTheDefaultStore()
    {
        // Arrange
        var access = TwoStores()
            .Add(FakeOutlookAccess.DefaultStore, "Drafts", FakeOutlookAccess.Item("d1", Local(2026, 11, 20, 9)))
            .Add(FakeOutlookAccess.DefaultStore, "Inbox/Projects", FakeOutlookAccess.Item("p1", Local(2026, 11, 21, 9)))
            .Add(FakeOutlookAccess.SharedStore, "Drafts", FakeOutlookAccess.Item("sd", Local(2026, 11, 21, 9)));
        var sut = Reader(access);

        // Act
        var result = sut.Count(["Inbox/Projects"]);

        // Assert
        result.Value.Should().Equal(new OutlookFolderCount("Inbox", 3), new OutlookFolderCount("Drafts", 1), new OutlookFolderCount("Inbox/Projects", 1));
    }

    [Fact]
    public void Count_UnknownNamedFolder_IsRefusedFolderNotFound()
    {
        // Arrange
        var access = TwoStores().AddFolder(FakeOutlookAccess.DefaultStore, "Drafts");
        var sut = Reader(access);

        // Act
        var result = sut.Count(["Nope"]);

        // Assert
        result.Refusal.Should().Be(OutlookRefusal.FolderNotFound);
    }

    [Fact]
    public void List_OutlookNotRunning_IsRefusedOutlookNotAvailable()
    {
        // Arrange
        var access = TwoStores();
        access.Unavailable = true;
        var sut = Reader(access);

        // Act
        var result = sut.List("Inbox", Since(2026, 11, 1));

        // Assert
        result.Message.Should().Be("Outlook not available");
    }
}
