namespace Zyggy.Core.Supervision;

/// <summary>What the owner is told (deliverable 18, "Waiting list"): a subject and one line per item. Built from ids, kinds and names only.</summary>
/// <param name="Subject">The one-line subject.</param>
/// <param name="Lines">The lines of the body, one per item; never a text an agent wrote.</param>
public sealed record OwnerNotice(string Subject, IReadOnlyList<string> Lines);

/// <summary>
/// Tells the owner something outside the bus. The first implementation is a mail to the owner's own mailbox; Telegram can follow
/// behind the same seam (founding spec §9). Nothing outside an implementation references the channel itself.
/// </summary>
public interface INotifier
{
    /// <summary>Sends <paramref name="notice"/> to the owner.</summary>
    /// <param name="notice">The notice: subject and lines, no text an agent wrote.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the channel has accepted the notice.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="notice"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the channel refuses or cannot be reached; the caller retries.</exception>
    Task NotifyAsync(OwnerNotice notice, CancellationToken ct);
}
