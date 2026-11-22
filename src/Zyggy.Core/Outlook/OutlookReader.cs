namespace Zyggy.Core.Outlook;

/// <summary>One listed mail (spec 58 "Reading"); every date is ISO 8601 with offset, written with the invariant culture.</summary>
/// <param name="Reference">The stable item reference (Outlook's entry id).</param>
/// <param name="SenderName">The sender's display name.</param>
/// <param name="SenderAddress">The sender's address.</param>
/// <param name="Subject">The subject.</param>
/// <param name="Received">The received date, <c>yyyy-MM-ddTHH:mm:sszzz</c>.</param>
/// <param name="Importance"><c>low</c>, <c>normal</c> or <c>high</c>.</param>
/// <param name="Flag"><c>none</c>, <c>flagged</c> or <c>complete</c>.</param>
/// <param name="IsRead">Whether the mail was read.</param>
/// <param name="HasAttachments">Whether it carries attachments.</param>
/// <param name="IsProtected">Whether it is encrypted or rights-protected (its body is never read).</param>
internal sealed record OutlookMailSummary(
    string Reference,
    string SenderName,
    string SenderAddress,
    string Subject,
    string Received,
    string Importance,
    string Flag,
    bool IsRead,
    bool HasAttachments,
    bool IsProtected);

/// <summary>One read mail: the listed fields, the attachments' names and count, and the plain-text body cut at the configured length.</summary>
/// <param name="Summary">The listed fields.</param>
/// <param name="AttachmentNames">The attachments' names; their content is never opened.</param>
/// <param name="Body">The plain-text body, or <see langword="null"/> for a protected mail.</param>
/// <param name="BodyTruncated">Whether the body was cut at <c>outlook.read.bodyMaxChars</c>.</param>
internal sealed record OutlookMail(OutlookMailSummary Summary, IReadOnlyList<string> AttachmentNames, string? Body, bool BodyTruncated)
{
    /// <summary>Gets the number of attachments.</summary>
    public int AttachmentCount => AttachmentNames.Count;
}

/// <summary>The item count of one folder.</summary>
/// <param name="Folder">The folder as asked.</param>
/// <param name="Count">Its item count.</param>
internal sealed record OutlookFolderCount(string Folder, int Count);

/// <summary>A read tool's outcome: the value, or the refusal.</summary>
/// <typeparam name="T">The value type.</typeparam>
/// <param name="Value">The value when not refused.</param>
/// <param name="Refusal">The refusal, or <see langword="null"/>.</param>
/// <param name="Detail">For <see cref="OutlookRefusal.OutlookNotAvailable"/>: the short reason Outlook gave (never mail content).</param>
internal sealed record OutlookResult<T>(T? Value, OutlookRefusal? Refusal, string? Detail = null)
    where T : class
{
    /// <summary>Gets the refusal as the tools print it (<c>policy not confirmed</c>, …), or <see langword="null"/>.</summary>
    public string? Message => Refusal is { } refusal ? OutlookReader.Describe(refusal) : null;
}

/// <summary>
/// The read path of the work mail (spec 58 slice 1): list, read and count mail of the default store through
/// <see cref="IOutlookAccess"/>, after the policy record and <c>outlook.enabled</c> allow a read; culture-proof dates.
/// </summary>
/// <param name="access">The Outlook access.</param>
/// <param name="policy">The evaluated policy.</param>
/// <param name="settings">The <c>outlook</c> settings (caps).</param>
/// <param name="zone">The time zone of the Outlook session (COM dates are local).</param>
internal sealed class OutlookReader(IOutlookAccess access, OutlookPolicyDecision policy, OutlookSettings settings, TimeZoneInfo zone)
{
    /// <summary>The folders <see cref="Count"/> always reports.</summary>
    public static readonly IReadOnlyList<string> StandardFolders = ["Inbox", "Drafts"];

    private const string ReadScope = "read";

    /// <summary>Lists the mail of a default-store folder received at or after <paramref name="since"/>, newest first, capped.</summary>
    /// <param name="folder">The folder (<c>Inbox</c> or a folder the owner names).</param>
    /// <param name="since">The lower bound.</param>
    /// <param name="max">The most items wanted; never more than <c>outlook.read.maxItems</c>.</param>
    /// <returns>The items, or the refusal.</returns>
    public OutlookResult<IReadOnlyList<OutlookMailSummary>> List(string folder, DateTimeOffset since, int? max = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        var cap = Math.Min(max is > 0 ? max.Value : settings.ReadMaxItems, settings.ReadMaxItems);
        return Guarded<IReadOnlyList<OutlookMailSummary>>(() =>
        {
            var store = access.DefaultStoreId();
            var sinceLocal = TimeZoneInfo.ConvertTime(since, zone).DateTime;
            var items = access.Items(store, folder, sinceLocal, cap);
            if (items is null)
            {
                return Refused<IReadOnlyList<OutlookMailSummary>>(OutlookRefusal.FolderNotFound);
            }

            var listed = items
                .Select(item => (Item: item, Received: OutlookDates.FromLocal(item.ReceivedLocal, zone)))
                .Where(x => x.Received >= since)
                .OrderByDescending(x => x.Received)
                .ThenBy(x => x.Item.EntryId, StringComparer.Ordinal)
                .Take(cap)
                .Select(x => Summarize(x.Item))
                .ToList();
            return new OutlookResult<IReadOnlyList<OutlookMailSummary>>(listed, null);
        });
    }

    /// <summary>Reads one item of the default store by its reference.</summary>
    /// <param name="reference">The item reference.</param>
    /// <returns>The mail, or the refusal.</returns>
    public OutlookResult<OutlookMail> Read(string reference)
    {
        ArgumentException.ThrowIfNullOrEmpty(reference);
        return Guarded(() =>
        {
            var store = access.DefaultStoreId();
            if (access.Find(store, reference) is not { } item)
            {
                return Refused<OutlookMail>(OutlookRefusal.ItemNotFound);
            }

            // A protected mail's body is never asked of Outlook (AC-4); attachments are only named, never opened.
            string? body = null;
            var truncated = false;
            if (!item.IsProtected)
            {
                var full = access.ReadBody(store, item.EntryId);
                truncated = full.Length > settings.ReadBodyMaxChars;
                body = truncated ? full[..settings.ReadBodyMaxChars] : full;
            }

            return new OutlookResult<OutlookMail>(new OutlookMail(Summarize(item), [.. item.AttachmentNames], body, truncated), null);
        });
    }

    /// <summary>Counts Inbox, Drafts and the named folders of the default store.</summary>
    /// <param name="folders">The owner's named folders, in addition to Inbox and Drafts.</param>
    /// <returns>The counts, or the refusal.</returns>
    public OutlookResult<IReadOnlyList<OutlookFolderCount>> Count(IReadOnlyList<string> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        return Guarded<IReadOnlyList<OutlookFolderCount>>(() =>
        {
            var store = access.DefaultStoreId();
            var counts = new List<OutlookFolderCount>();
            foreach (var folder in StandardFolders.Concat(folders).Distinct(StringComparer.Ordinal))
            {
                if (access.Count(store, folder) is not { } count)
                {
                    return Refused<IReadOnlyList<OutlookFolderCount>>(OutlookRefusal.FolderNotFound);
                }

                counts.Add(new OutlookFolderCount(folder, count));
            }

            return new OutlookResult<IReadOnlyList<OutlookFolderCount>>(counts, null);
        });
    }

    /// <summary>Returns the text a tool prints for a refusal.</summary>
    /// <param name="refusal">The refusal.</param>
    /// <returns>The text.</returns>
    public static string Describe(OutlookRefusal refusal) => refusal switch
    {
        OutlookRefusal.PolicyNotConfirmed => "policy not confirmed",
        OutlookRefusal.NotEnabled => "outlook not enabled",
        OutlookRefusal.OutlookNotAvailable => "Outlook not available",
        OutlookRefusal.FolderNotFound => "folder not found",
        OutlookRefusal.ItemNotFound => "item not found",
        _ => throw new ArgumentOutOfRangeException(nameof(refusal), refusal, null),
    };

    private static OutlookResult<T> Refused<T>(OutlookRefusal refusal)
        where T : class => new(null, refusal);

    private static string ImportanceWord(int importance) => importance switch
    {
        0 => "low",
        2 => "high",
        _ => "normal",
    };

    private static string FlagWord(int flag) => flag switch
    {
        1 => "complete",
        2 => "flagged",
        _ => "none",
    };

    // The policy is checked before Outlook is touched (AC-5); an unreachable Outlook becomes a refusal, never an exception.
    private OutlookResult<T> Guarded<T>(Func<OutlookResult<T>> read)
        where T : class
    {
        if (policy.RefusalFor(ReadScope) is { } refusal)
        {
            return Refused<T>(refusal);
        }

        try
        {
            return read();
        }
        catch (OutlookUnavailableException ex)
        {
            return new OutlookResult<T>(null, OutlookRefusal.OutlookNotAvailable, ex.Message);
        }
    }

    private OutlookMailSummary Summarize(OutlookRawItem item) => new(
        item.EntryId,
        item.SenderName,
        item.SenderAddress,
        item.Subject,
        OutlookDates.Write(item.ReceivedLocal, zone),
        ImportanceWord(item.Importance),
        FlagWord(item.FlagStatus),
        IsRead: !item.UnRead,
        HasAttachments: item.AttachmentNames.Count > 0,
        item.IsProtected);
}
