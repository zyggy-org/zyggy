using System.Dynamic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Zyggy.Core.Outlook;

namespace Zyggy.Core.Tests.Outlook;

/// <summary>
/// The COM access's own thread and loop (spec 58 slice 2, corrections of the slice-2 verify), over a fake late-bound Outlook object
/// (a dynamic object graph in place of <c>Outlook.Application</c>; no real Outlook): every failure on the COM thread becomes
/// "Outlook not available" (never a crash of the process), a call that gets no answer within the call timeout is "Outlook asked for
/// access", and <c>outlook.read.maxItems</c> stops the item loop instead of reading the whole date window.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OutlookComAccessTests
{
    private const string Store = "store-default";

    private static readonly TimeSpan Long = TimeSpan.FromSeconds(30);

    // ---- failures on the COM thread (correction 1) ----

    public static TheoryData<Exception> ThreadFailures => new()
    {
        new UnauthorizedAccessException("Access is denied"),
        Marshal.GetExceptionForHR(unchecked((int)0x80080005))!, // CO_E_SERVER_EXEC_FAILURE: a COMException
        new ArgumentException("bad argument"),
        new FormatException("unexpected value"),
    };

    [Theory]
    [MemberData(nameof(ThreadFailures))]
    public void DefaultStoreId_CreatingOutlookThrows_IsOutlookUnavailable(Exception failure)
    {
        // Arrange
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the COM access runs on an STA thread, Windows only");
        var sut = new OutlookComAccess(Long, () => throw failure);

        // Act
        var act = () => sut.DefaultStoreId();

        // Assert
        act.Should().Throw<OutlookUnavailableException>().WithInnerException(failure.GetType());
    }

    [Theory]
    [MemberData(nameof(ThreadFailures))]
    public void DefaultStoreId_OutlookThrowsDuringTheCall_IsOutlookUnavailable(Exception failure)
    {
        // Arrange
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the COM access runs on an STA thread, Windows only");
        var sut = new OutlookComAccess(Long, () => new ThrowingApplication(failure));

        // Act
        var act = () => sut.DefaultStoreId();

        // Assert
        act.Should().Throw<OutlookUnavailableException>().WithInnerException(failure.GetType());
    }

    // ---- the call timeout (correction 3) ----

    [Fact]
    public void DefaultCallTimeout_IsOneHundredTwentySeconds()
    {
        // Assert
        OutlookComAccess.DefaultCallTimeout.Should().Be(TimeSpan.FromSeconds(120));
    }

    [Fact]
    public void Constructor_DefaultCallTimeout_IsTheWaitOfEveryCall()
    {
        // Arrange
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the COM access runs on an STA thread, Windows only");

        // Act
        var sut = new OutlookComAccess();

        // Assert
        sut.CallTimeout.Should().Be(TimeSpan.FromSeconds(120));
    }

    [Fact]
    public void DefaultStoreId_OutlookDoesNotAnswerWithinTheTimeout_IsOutlookAskedForAccess()
    {
        // Arrange
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the COM access runs on an STA thread, Windows only");
        using var prompt = new ManualResetEventSlim(false);
        var timeout = TimeSpan.FromMilliseconds(100);
        var sut = new OutlookComAccess(timeout, () =>
        {
            prompt.Wait(Long);
            return Application(Folder());
        });

        // Act
        var act = () => sut.DefaultStoreId();

        // Assert
        try
        {
            act.Should().Throw<OutlookPromptTimeoutException>().Which.Waited.Should().Be(timeout);
        }
        finally
        {
            prompt.Set();
        }
    }

    [Fact]
    public void DefaultStoreId_OutlookAnswersWithinTheTimeout_ReturnsTheStore()
    {
        // Arrange
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the COM access runs on an STA thread, Windows only");
        var sut = new OutlookComAccess(Long, () => Application(Folder()));

        // Act
        var store = sut.DefaultStoreId();

        // Assert
        store.Should().Be(Store);
    }

    // ---- maxItems inside the loop (correction 2) ----

    [Fact]
    public void Items_FiftyMailsInTheWindowMaxThree_FetchesOnlyThree()
    {
        // Arrange
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the COM access runs on an STA thread, Windows only");
        var folder = Folder(Enumerable.Range(0, 50).Select(i => Mail("m" + i, new DateTime(2026, 11, 21, 12, 0, 0).AddMinutes(-i))).ToArray());
        var sut = new OutlookComAccess(Long, () => Application(folder));

        // Act
        var items = sut.Items(Store, "Inbox", new DateTime(2026, 11, 1), max: 3)!.ToList();

        // Assert
        (items.Select(i => i.EntryId).ToList(), folder.Fetched).Should().BeEquivalentTo((new List<string> { "m0", "m1", "m2" }, 3));
    }

    [Fact]
    public void Items_NonMailItems_AreSkippedAndNotCounted()
    {
        // Arrange
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the COM access runs on an STA thread, Windows only");
        var at = new DateTime(2026, 11, 21, 12, 0, 0);
        var folder = Folder(Mail("m0", at), Meeting(at.AddMinutes(-1)), Mail("m1", at.AddMinutes(-2)), Mail("m2", at.AddMinutes(-3)));
        var sut = new OutlookComAccess(Long, () => Application(folder));

        // Act
        var items = sut.Items(Store, "Inbox", new DateTime(2026, 11, 1), max: 2)!;

        // Assert
        items.Select(i => i.EntryId).Should().Equal("m0", "m1");
    }

    [Fact]
    public void Items_StopsAtTheFirstMailBeforeTheBound()
    {
        // Arrange
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the COM access runs on an STA thread, Windows only");
        var folder = Folder(Mail("m0", new DateTime(2026, 11, 21)), Mail("old", new DateTime(2026, 10, 1)), Mail("older", new DateTime(2026, 9, 1)));
        var sut = new OutlookComAccess(Long, () => Application(folder));

        // Act
        var items = sut.Items(Store, "Inbox", new DateTime(2026, 11, 1), max: 50)!.ToList();

        // Assert
        (items.Select(i => i.EntryId).ToList(), folder.Fetched).Should().BeEquivalentTo((new List<string> { "m0" }, 2));
    }

    // ---- the fake late-bound Outlook ----

    private static FakeFolder Folder(params ExpandoObject[] mail) => new(mail);

    private static ExpandoObject Application(FakeFolder inbox)
    {
        dynamic store = new ExpandoObject();
        store.StoreID = Store;
        store.GetDefaultFolder = new Func<int, object>(_ => inbox.Dynamic);
        store.GetRootFolder = new Func<object>(() => inbox.Dynamic);

        dynamic stores = new ExpandoObject();
        stores.Count = 1;
        stores.Item = new Func<int, object>(_ => store);

        dynamic session = new ExpandoObject();
        session.DefaultStore = store;
        session.Stores = stores;

        dynamic app = new ExpandoObject();
        app.Session = session;
        return app;
    }

    private static ExpandoObject Mail(string entryId, DateTime received)
    {
        dynamic attachments = new ExpandoObject();
        attachments.Count = 0;
        attachments.Item = new Func<int, object>(_ => throw new InvalidOperationException("no attachment"));

        dynamic mail = new ExpandoObject();
        mail.Class = 43;
        mail.EntryID = entryId;
        mail.SenderName = "Sender " + entryId;
        mail.SenderEmailAddress = entryId + "@example.test";
        mail.SenderEmailType = "SMTP";
        mail.Subject = "Subject " + entryId;
        mail.ReceivedTime = received;
        mail.Importance = 1;
        mail.FlagStatus = 0;
        mail.UnRead = true;
        mail.MessageClass = "IPM.Note";
        mail.Attachments = attachments;
        return mail;
    }

    private static ExpandoObject Meeting(DateTime received)
    {
        dynamic meeting = new ExpandoObject();
        meeting.Class = 53;
        meeting.ReceivedTime = received;
        return meeting;
    }

    /// <summary>A folder whose <c>Items</c> counts every item it hands out, as Outlook's <c>Items.Item(i)</c> fetches it.</summary>
    private sealed class FakeFolder
    {
        public FakeFolder(ExpandoObject[] mail)
        {
            dynamic items = new ExpandoObject();
            items.Count = mail.Length;
            items.Sort = new Action<string, bool>((_, _) => { });
            items.Item = new Func<int, object>(i =>
            {
                Fetched++;
                return mail[i - 1];
            });

            dynamic folder = new ExpandoObject();
            folder.Items = items;
            folder.Name = "Inbox";
            Dynamic = folder;
        }

        public object Dynamic { get; }

        public int Fetched { get; private set; }
    }

    /// <summary>An <c>Outlook.Application</c> whose every member throws, as a COM call refused by Outlook or by policy does.</summary>
    private sealed class ThrowingApplication(Exception failure) : DynamicObject
    {
        public override bool TryGetMember(GetMemberBinder binder, out object? result) => throw failure;
    }
}
