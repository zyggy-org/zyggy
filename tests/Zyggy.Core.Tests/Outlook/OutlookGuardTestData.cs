using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Outlook;

using static Zyggy.Core.Tests.Outlook.OutlookTestData;

namespace Zyggy.Core.Tests.Outlook;

/// <summary>A list in place of the laptop's action log file.</summary>
internal sealed class RecordingActionLog : IOutlookActionLog
{
    public List<OutlookActionLogRow> Rows { get; } = [];

    public void Append(OutlookActionLogRow row) => Rows.Add(row);
}

/// <summary>Shared builders for the outlook guard tests (spec 58 slice 6).</summary>
internal static class OutlookGuardTestData
{
    public static readonly DateTimeOffset Now = new(2026, 11, 22, 9, 30, 0, TimeSpan.Zero);

    public static readonly OutlookRunContext Attended = new(Attended: true);

    public static OutlookActionSettings SendAndMove(int maxRecipients = OutlookActionSettings.DefaultSendMaxRecipients, int bodyMaxChars = OutlookActionSettings.DefaultSendBodyMaxChars, string[]? excluded = null) =>
        new(new HashSet<string>(["send", "move"], StringComparer.Ordinal), maxRecipients, bodyMaxChars, excluded ?? []);

    /// <summary>A default store with an Inbox mail m1, the well-known folders, a project folder and an excluded folder; a shared store with s1.</summary>
    public static FakeOutlookAccess Mailbox() =>
        new FakeOutlookAccess()
            .Add(FakeOutlookAccess.DefaultStore, "Inbox", FakeOutlookAccess.Item("m1", Local(2026, 11, 21, 9)), body: "body")
            .AddFolder(FakeOutlookAccess.DefaultStore, "Deleted Items")
            .AddFolder(FakeOutlookAccess.DefaultStore, "Archive")
            .AddFolder(FakeOutlookAccess.DefaultStore, "Inbox/Projects")
            .AddFolder(FakeOutlookAccess.DefaultStore, "Private")
            .AddFolder(FakeOutlookAccess.DefaultStore, "Private/Health")
            .AddFolder(FakeOutlookAccess.DefaultStore, "Recoverable Items")
            .Add(FakeOutlookAccess.SharedStore, "Inbox", FakeOutlookAccess.Item("s1", Local(2026, 11, 21, 9)), body: "shared")
            .AddFolder(FakeOutlookAccess.SharedStore, "Archive");

    public static OutlookGuard Guard(
        FakeOutlookAccess access,
        RecordingActionLog log,
        OutlookPolicyDecision? policy = null,
        OutlookActionSettings? settings = null,
        OutlookRunContext? run = null) =>
        new(access, policy ?? Allowed, settings ?? SendAndMove(), run ?? Attended, log, new FakeTimeProvider(Now));

    /// <summary>A send within every rule: a plain-text reply with its quoted history to one recipient.</summary>
    public static OutlookSendRequest Reply() =>
        new(["colleague@example.test"], [], "RE: Planning", "Thanks, Thursday works.\n\n> From: colleague@example.test\n> Can we meet Thursday?")
        {
            InReplyTo = "m1",
        };
}
