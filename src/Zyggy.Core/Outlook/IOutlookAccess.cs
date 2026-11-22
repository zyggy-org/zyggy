namespace Zyggy.Core.Outlook;

/// <summary>
/// The thin, internal access to the classic Outlook of the work laptop (spec 58 D-Q3): one implementation over COM interop
/// (slice 2), a fake in tests. It mirrors what COM offers and holds no rule; <see cref="OutlookReader"/> keeps the rules
/// (default store only, since-date, newest first, caps, body cut, protected mail, invariant dates).
/// </summary>
/// <remarks>
/// There is deliberately no member that opens, reads or saves an attachment (spec 58 AC-4): an item carries only the names
/// of its attachments. Every member may throw <see cref="OutlookUnavailableException"/> when Outlook is not running or the
/// profile is not loaded.
/// </remarks>
internal interface IOutlookAccess
{
    /// <summary>Returns the store id of the profile's default store (the owner's work mailbox).</summary>
    /// <returns>The store id.</returns>
    string DefaultStoreId();

    /// <summary>
    /// Returns the items of a folder of <paramref name="storeId"/> received at or after <paramref name="sinceLocal"/>: the newest
    /// <paramref name="max"/> of them, the cap applied while reading so a wide date window is never read whole. A fake may return
    /// more in any order (the reader filters, orders and caps again); the COM access stops after <paramref name="max"/> mail items.
    /// </summary>
    /// <param name="storeId">The store id.</param>
    /// <param name="folder"><c>Inbox</c>, <c>Drafts</c>, or a folder path of that store (<c>Inbox/Projects</c>).</param>
    /// <param name="sinceLocal">The lower bound in the local time of the Outlook session.</param>
    /// <param name="max">The most mail items to read (<c>outlook.read.maxItems</c>, or fewer when the caller asks fewer).</param>
    /// <param name="beforeLocal">
    /// An exclusive upper bound in the same local time, or <see langword="null"/> for none: items received at or after it are skipped
    /// and do not count toward <paramref name="max"/> (the backlog of a capped brief).
    /// </param>
    /// <returns>The items, or <see langword="null"/> when the folder does not exist in that store.</returns>
    IEnumerable<OutlookRawItem>? Items(string storeId, string folder, DateTime sinceLocal, int max, DateTime? beforeLocal = null);

    /// <summary>Counts the items of a folder of <paramref name="storeId"/>.</summary>
    /// <param name="storeId">The store id.</param>
    /// <param name="folder">The folder, as for <see cref="Items"/>.</param>
    /// <returns>The count, or <see langword="null"/> when the folder does not exist in that store.</returns>
    int? Count(string storeId, string folder);

    /// <summary>Finds one item by its entry id, in <paramref name="storeId"/> only.</summary>
    /// <param name="storeId">The store id.</param>
    /// <param name="entryId">The item reference (Outlook's entry id).</param>
    /// <returns>The item, or <see langword="null"/> when that store holds no such item.</returns>
    OutlookRawItem? Find(string storeId, string entryId);

    /// <summary>Reads the plain-text body of one item of <paramref name="storeId"/> (Outlook's protected <c>Body</c> property).</summary>
    /// <param name="storeId">The store id.</param>
    /// <param name="entryId">The item reference.</param>
    /// <returns>The plain-text body.</returns>
    string ReadBody(string storeId, string entryId);

    /// <summary>
    /// Sends one plain-text message from the profile's default account (spec 58 slice 7): To and Cc as given, no Bcc, no attachment,
    /// no other account, sender or reply-to — the members exist only for what the guard passed. Sent Items keeps the copy.
    /// </summary>
    /// <param name="message">The message, exactly as the guard checked it and the prompt showed it.</param>
    void Send(OutlookOutgoing message);

    /// <summary>Moves one mail of <paramref name="storeId"/> to a folder of the same store (spec 58 slice 7); never deletes.</summary>
    /// <param name="storeId">The store id (the default store).</param>
    /// <param name="entryId">The item reference.</param>
    /// <param name="folder">The destination folder, as for <see cref="Items"/>.</param>
    /// <returns>Whether the mail was moved; <see langword="false"/> when the item or the folder is not in that store.</returns>
    bool Move(string storeId, string entryId, string folder);
}

/// <summary>One plain-text message to send (spec 58 "Send"): nothing else a send could carry is representable.</summary>
/// <param name="To">The To addresses.</param>
/// <param name="Cc">The Cc addresses.</param>
/// <param name="Subject">The subject.</param>
/// <param name="Body">The whole plain-text body, the quoted history of a reply included.</param>
internal sealed record OutlookOutgoing(IReadOnlyList<string> To, IReadOnlyList<string> Cc, string Subject, string Body);

/// <summary>One mail item as Outlook gives it, before any rule is applied.</summary>
/// <param name="EntryId">Outlook's entry id: the stable item reference.</param>
/// <param name="SenderName">The sender's display name.</param>
/// <param name="SenderAddress">The sender's address (SMTP where Outlook resolves it).</param>
/// <param name="Subject">The subject.</param>
/// <param name="ReceivedLocal">The received time as COM gives it: local time of the Outlook session, unspecified kind.</param>
/// <param name="Importance">COM <c>OlImportance</c>: 0 low, 1 normal, 2 high.</param>
/// <param name="FlagStatus">COM <c>OlFlagStatus</c>: 0 none, 1 complete, 2 marked.</param>
/// <param name="UnRead">Whether the item is unread.</param>
/// <param name="AttachmentNames">The attachments' file names; never their content.</param>
/// <param name="IsProtected">Whether the item is encrypted or rights-protected (its body is never read).</param>
internal sealed record OutlookRawItem(
    string EntryId,
    string SenderName,
    string SenderAddress,
    string Subject,
    DateTime ReceivedLocal,
    int Importance,
    int FlagStatus,
    bool UnRead,
    IReadOnlyList<string> AttachmentNames,
    bool IsProtected);

/// <summary>Outlook is not running, its profile is not loaded, or it refused the call (spec 58 "Outlook not reachable").</summary>
internal sealed class OutlookUnavailableException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="OutlookUnavailableException"/> class.</summary>
    public OutlookUnavailableException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="OutlookUnavailableException"/> class.</summary>
    /// <param name="message">The short reason.</param>
    public OutlookUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="OutlookUnavailableException"/> class.</summary>
    /// <param name="message">The short reason.</param>
    /// <param name="innerException">The COM error.</param>
    public OutlookUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
