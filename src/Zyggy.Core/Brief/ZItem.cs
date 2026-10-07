namespace Zyggy.Core.Brief;

/// <summary>What a Z item does when the owner says "do Z&lt;n&gt;" (spec 35 R2.1, AC-17).</summary>
internal enum ZKind
{
    Send,
    Move,
    DiscardDraft,
    FileOther,
}

/// <summary>Where a move goes; recoverable deletions and purges are never a destination.</summary>
internal enum ZDestination
{
    Archive,
    DeletedItems,
}

/// <summary>A mail's sender: the name is printed, the address lives in the sidecar only (R2.4).</summary>
internal sealed record ZSender(string Name, string Address);

/// <summary>
/// One numbered item of "I can do these": a reply to send (its draft), a mail to move, an earlier reply draft to discard, or the one
/// item that files every "other" mail (<see cref="MessageIds"/>). Addresses appear only in the sidecar.
/// </summary>
internal sealed record ZItem(
    int N,
    ZKind Kind,
    string? MessageId,
    IReadOnlyList<string>? MessageIds,
    string? DraftId,
    ZDestination? Destination,
    string Subject,
    ZSender Sender,
    string Received,
    string Why,
    bool FromUrgent);
