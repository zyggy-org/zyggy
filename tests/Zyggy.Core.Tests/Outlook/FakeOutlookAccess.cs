using Zyggy.Core.Outlook;

namespace Zyggy.Core.Tests.Outlook;

/// <summary>
/// A fake Outlook profile with two stores — the default store (the work mailbox) and a shared mailbox — that records every
/// call, so a test can prove which store was touched and that a refused tool touched nothing (spec 58 AC-2, AC-4, AC-5).
/// </summary>
internal sealed class FakeOutlookAccess : IOutlookAccess
{
    public const string DefaultStore = "store-default";
    public const string SharedStore = "store-shared";

    private readonly Dictionary<(string Store, string Folder), List<OutlookRawItem>> _folders = [];
    private readonly Dictionary<(string Store, string EntryId), string> _bodies = [];

    public List<string> Calls { get; } = [];

    public bool Unavailable { get; set; }

    public static OutlookRawItem Item(
        string entryId,
        DateTime receivedLocal,
        string subject = "Subject",
        int importance = 1,
        int flag = 0,
        bool unread = true,
        string[]? attachments = null,
        bool isProtected = false) =>
        new(entryId, "Sender " + entryId, entryId + "@example.test", subject, receivedLocal, importance, flag, unread, attachments ?? [], isProtected);

    public FakeOutlookAccess Add(string store, string folder, OutlookRawItem item, string body = "")
    {
        if (!_folders.TryGetValue((store, folder), out var items))
        {
            items = [];
            _folders[(store, folder)] = items;
        }

        items.Add(item);
        _bodies[(store, item.EntryId)] = body;
        return this;
    }

    public FakeOutlookAccess AddFolder(string store, string folder)
    {
        _folders.TryAdd((store, folder), []);
        return this;
    }

    public string DefaultStoreId()
    {
        Record("DefaultStoreId");
        return DefaultStore;
    }

    /// <summary>Gets the <c>max</c> of the last <see cref="Items"/> call.</summary>
    public int? MaxAsked { get; private set; }

    // Returns every item of the folder, unordered and unfiltered: the reader owns the since filter, the order and the cap (it
    // records the cap it was given, which the COM access applies inside its loop).
    /// <summary>Gets the <c>beforeLocal</c> of the last <see cref="Items"/> call.</summary>
    public DateTime? BeforeAsked { get; private set; }

    public IEnumerable<OutlookRawItem>? Items(string storeId, string folder, DateTime sinceLocal, int max, DateTime? beforeLocal = null)
    {
        MaxAsked = max;
        BeforeAsked = beforeLocal;
        Record($"Items {storeId} {folder}");
        return _folders.TryGetValue((storeId, folder), out var items) ? [.. items.OrderBy(i => i.EntryId, StringComparer.Ordinal)] : null;
    }

    public int? Count(string storeId, string folder)
    {
        Record($"Count {storeId} {folder}");
        return _folders.TryGetValue((storeId, folder), out var items) ? items.Count : null;
    }

    public OutlookRawItem? Find(string storeId, string entryId)
    {
        Record($"Find {storeId} {entryId}");
        return _folders.Where(f => f.Key.Store == storeId).SelectMany(f => f.Value).FirstOrDefault(i => i.EntryId == entryId);
    }

    public string ReadBody(string storeId, string entryId)
    {
        Record($"ReadBody {storeId} {entryId}");
        return _bodies[(storeId, entryId)];
    }

    /// <summary>Gets the messages sent, in order (spec 58 slice 7): what Outlook would have sent.</summary>
    public List<OutlookOutgoing> Sent { get; } = [];

    /// <summary>Gets or sets a value indicating whether a send or a move fails in Outlook (the item and folders stay as they are).</summary>
    public bool ActionsFail { get; set; }

    public void Send(OutlookOutgoing message)
    {
        Record("Send");
        if (ActionsFail)
        {
            throw new OutlookUnavailableException("the send failed");
        }

        Sent.Add(message);
    }

    public bool Move(string storeId, string entryId, string folder)
    {
        Record($"Move {storeId} {entryId} {folder}");
        if (ActionsFail)
        {
            throw new OutlookUnavailableException("the move failed");
        }

        var from = _folders.FirstOrDefault(f => f.Key.Store == storeId && f.Value.Exists(i => i.EntryId == entryId));
        if (from.Value is null || !_folders.TryGetValue((storeId, folder), out var to))
        {
            return false;
        }

        var item = from.Value.Single(i => i.EntryId == entryId);
        from.Value.Remove(item);
        to.Add(item);
        return true;
    }

    /// <summary>Returns the folder of the default store that holds <paramref name="entryId"/>, or <see langword="null"/>.</summary>
    public string? FolderOf(string entryId) =>
        _folders.Where(f => f.Key.Store == DefaultStore && f.Value.Exists(i => i.EntryId == entryId)).Select(f => f.Key.Folder).FirstOrDefault();

    private void Record(string call)
    {
        Calls.Add(call);
        if (Unavailable)
        {
            throw new OutlookUnavailableException("Outlook is not running");
        }
    }
}
