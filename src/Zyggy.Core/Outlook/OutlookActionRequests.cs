namespace Zyggy.Core.Outlook;

/// <summary>The two outlook actions (spec 58 "Actions: send and move"); nothing else is ever provided.</summary>
internal enum OutlookAction
{
    /// <summary>Send one plain-text message from the default account.</summary>
    Send,

    /// <summary>Move (file) one mail to a folder of the default store.</summary>
    Move,
}

/// <summary>The body format a send asks for; only <see cref="PlainText"/> passes the guard.</summary>
internal enum OutlookBodyFormat
{
    /// <summary>Plain text.</summary>
    PlainText,

    /// <summary>HTML.</summary>
    Html,

    /// <summary>Rich text (RTF).</summary>
    RichText,
}

/// <summary>
/// One send as the action tool asks it (spec 58 "Send"), before the guard: every field a send could carry is named, so the guard
/// can refuse what the policy does not allow instead of never seeing it.
/// </summary>
/// <param name="To">The To recipients (addresses).</param>
/// <param name="Cc">The Cc recipients (addresses).</param>
/// <param name="Subject">The subject.</param>
/// <param name="Body">The whole body, the quoted history of a reply included.</param>
internal sealed record OutlookSendRequest(IReadOnlyList<string> To, IReadOnlyList<string> Cc, string Subject, string Body)
{
    /// <summary>Gets the Bcc recipients; any is refused.</summary>
    public IReadOnlyList<string> Bcc { get; init; } = [];

    /// <summary>Gets the body format; anything but plain text is refused.</summary>
    public OutlookBodyFormat BodyFormat { get; init; } = OutlookBodyFormat.PlainText;

    /// <summary>Gets the attachments' file names; any is refused.</summary>
    public IReadOnlyList<string> Attachments { get; init; } = [];

    /// <summary>Gets another sending account (Outlook's <c>SendUsingAccount</c>); any value is refused.</summary>
    public string? SendingAccount { get; init; }

    /// <summary>Gets another sender (Outlook's <c>SentOnBehalfOfName</c>); any value is refused.</summary>
    public string? Sender { get; init; }

    /// <summary>Gets reply-to addresses (Outlook's <c>ReplyRecipients</c>); any is refused.</summary>
    public IReadOnlyList<string> ReplyTo { get; init; } = [];

    /// <summary>Gets the reference of the mail this send answers, if it is a reply (logged, never acted on).</summary>
    public string? InReplyTo { get; init; }
}

/// <summary>One move as the action tool asks it (spec 58 "Move (file)"), before the guard.</summary>
/// <param name="Reference">The item reference of the mail to move.</param>
/// <param name="Destination">The destination folder (<c>Deleted Items</c>, <c>Archive</c>, <c>Inbox</c> or a folder path of the default store).</param>
internal sealed record OutlookMoveRequest(string Reference, string Destination)
{
    /// <summary>Gets the destination store id; <see langword="null"/> is the default store, any other store is refused.</summary>
    public string? Store { get; init; }

    /// <summary>Gets a value indicating whether the move asks for a permanent delete; always refused.</summary>
    public bool Permanent { get; init; }
}
