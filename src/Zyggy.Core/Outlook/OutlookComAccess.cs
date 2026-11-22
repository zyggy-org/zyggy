using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Zyggy.Core.Outlook;

/// <summary>
/// Outlook did not answer a call within the timeout: its programmatic-access security prompt is open, or Outlook hangs (spec 58
/// failure mode "Outlook security prompt"). The tool reports "Outlook asked for access"; the owner answers the prompt or stops.
/// </summary>
internal sealed class OutlookPromptTimeoutException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="OutlookPromptTimeoutException"/> class.</summary>
    public OutlookPromptTimeoutException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="OutlookPromptTimeoutException"/> class.</summary>
    /// <param name="message">The message.</param>
    public OutlookPromptTimeoutException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="OutlookPromptTimeoutException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The inner exception.</param>
    public OutlookPromptTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="OutlookPromptTimeoutException"/> class for a call that waited <paramref name="waited"/>.</summary>
    /// <param name="waited">How long the call waited.</param>
    public OutlookPromptTimeoutException(TimeSpan waited)
        : base(string.Create(CultureInfo.InvariantCulture, $"no answer within {(int)waited.TotalSeconds} s"))
    {
        Waited = waited;
    }

    /// <summary>Gets how long the call waited.</summary>
    public TimeSpan Waited { get; }
}

/// <summary>The access on a machine where Outlook cannot run (every OS but Windows): every call is "Outlook not available".</summary>
/// <param name="reason">The short reason.</param>
internal sealed class OutlookAccessUnavailable(string reason) : IOutlookAccess
{
    /// <inheritdoc/>
    public string DefaultStoreId() => throw new OutlookUnavailableException(reason);

    /// <inheritdoc/>
    public IEnumerable<OutlookRawItem>? Items(string storeId, string folder, DateTime sinceLocal, int max) => throw new OutlookUnavailableException(reason);

    /// <inheritdoc/>
    public int? Count(string storeId, string folder) => throw new OutlookUnavailableException(reason);

    /// <inheritdoc/>
    public OutlookRawItem? Find(string storeId, string entryId) => throw new OutlookUnavailableException(reason);

    /// <inheritdoc/>
    public string ReadBody(string storeId, string entryId) => throw new OutlookUnavailableException(reason);
}

/// <summary>
/// The one real <see cref="IOutlookAccess"/> (spec 58 D-Q3 (a)): classic Outlook through the runtime's built-in COM interop,
/// late-bound (<c>Outlook.Application</c> by ProgID, members through <c>IDispatch</c>), so no interop assembly ships. Each call runs
/// on its own STA thread with the Outlook object created and released there, and gives up after <paramref name="callTimeout"/>
/// (Outlook's security prompt blocks the call; the thread is abandoned, the process ends without it). Nothing here applies a
/// rule: <see cref="OutlookReader"/> filters, orders, caps and cuts.
/// </summary>
/// <param name="callTimeout">How long one call may wait for Outlook.</param>
/// <param name="createApplication">Creates the <c>Outlook.Application</c> object on the call's thread (a fake late-bound object in tests).</param>
[SupportedOSPlatform("windows")]
internal sealed class OutlookComAccess(TimeSpan callTimeout, Func<object> createApplication) : IOutlookAccess
{
    /// <summary>The wait before a call is reported as Outlook's unanswered prompt.</summary>
    public static readonly TimeSpan DefaultCallTimeout = TimeSpan.FromSeconds(120);

    private const string ProgId = "Outlook.Application";
    private const int OlMail = 43;
    private const string SmtpAddressProperty = "http://schemas.microsoft.com/mapi/proptag/0x5D01001F";

    /// <summary>Initializes a new instance of the <see cref="OutlookComAccess"/> class over the real Outlook, waiting <see cref="DefaultCallTimeout"/> per call.</summary>
    public OutlookComAccess()
        : this(DefaultCallTimeout, CreateOutlook)
    {
    }

    /// <summary>Gets how long one call waits for Outlook before it is reported as Outlook's unanswered prompt.</summary>
    public TimeSpan CallTimeout => callTimeout;

    /// <inheritdoc/>
    public string DefaultStoreId() => Invoke(app => (string)app.Session.DefaultStore.StoreID);

    /// <inheritdoc/>
    public IEnumerable<OutlookRawItem>? Items(string storeId, string folder, DateTime sinceLocal, int max) => Invoke<IEnumerable<OutlookRawItem>?>(app =>
    {
        dynamic? target = FindFolder(app, storeId, folder);
        if (target is null)
        {
            return null;
        }

        // Newest first, stop at the first item before the bound or once max mail items are read: no Restrict filter, whose date
        // text would depend on the session's locale, and never the whole date window when the cap is smaller.
        dynamic items = target.Items;
        items.Sort("[ReceivedTime]", true);
        int count = items.Count;
        var listed = new List<OutlookRawItem>();
        for (var i = 1; i <= count && listed.Count < max; i++)
        {
            dynamic item = items.Item(i);
            if ((int)item.Class != OlMail)
            {
                continue;
            }

            DateTime received = item.ReceivedTime;
            if (received < sinceLocal)
            {
                break;
            }

            listed.Add(Map(item));
        }

        return listed;
    });

    /// <inheritdoc/>
    public int? Count(string storeId, string folder) => Invoke<int?>(app => FindFolder(app, storeId, folder) is { } target ? (int)target.Items.Count : null);

    /// <inheritdoc/>
    public OutlookRawItem? Find(string storeId, string entryId) => Invoke<OutlookRawItem?>(app => FindItem(app, storeId, entryId) is { } item ? Map(item) : null);

    /// <inheritdoc/>
    public string ReadBody(string storeId, string entryId) => Invoke(app =>
        FindItem(app, storeId, entryId) is { } item ? (string?)item.Body ?? string.Empty : throw new OutlookUnavailableException("item not found"));

    private static dynamic? FindItem(dynamic app, string storeId, string entryId)
    {
        dynamic item;
        try
        {
            item = app.Session.GetItemFromID(entryId, storeId);
        }
        catch (COMException)
        {
            // An id that names no item of that store.
            return null;
        }

        return item is not null && (int)item.Class == OlMail ? item : null;
    }

    private static dynamic? FindFolder(dynamic app, string storeId, string folderPath)
    {
        IReadOnlyList<string> segments = OutlookComRules.Segments(folderPath);
        if (segments.Count == 0)
        {
            return null;
        }

        dynamic? store = FindStore(app, storeId) ?? throw new OutlookUnavailableException("the default store is not in the profile");
        dynamic? current = OutlookComRules.WellKnownFolder(segments[0]) is { } kind ? store.GetDefaultFolder(kind) : Child(store.GetRootFolder(), segments[0]);
        foreach (var segment in segments.Skip(1))
        {
            if (current is null)
            {
                return null;
            }

            current = Child(current, segment);
        }

        return current;
    }

    private static dynamic? FindStore(dynamic app, string storeId)
    {
        dynamic stores = app.Session.Stores;
        int count = stores.Count;
        for (var i = 1; i <= count; i++)
        {
            dynamic store = stores.Item(i);
            if ((string)store.StoreID == storeId)
            {
                return store;
            }
        }

        return null;
    }

    private static dynamic? Child(dynamic folder, string name)
    {
        dynamic folders = folder.Folders;
        int count = folders.Count;
        for (var i = 1; i <= count; i++)
        {
            dynamic child = folders.Item(i);
            if (string.Equals((string)child.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return child;
            }
        }

        return null;
    }

    // Attachments are named, never opened or saved (AC-4).
    private static OutlookRawItem Map(dynamic item)
    {
        var names = new List<string>();
        dynamic attachments = item.Attachments;
        int count = attachments.Count;
        for (var i = 1; i <= count; i++)
        {
            names.Add((string?)attachments.Item(i).FileName ?? string.Empty);
        }

        string messageClass = (string?)item.MessageClass ?? string.Empty;
        return new OutlookRawItem(
            (string)item.EntryID,
            (string?)item.SenderName ?? string.Empty,
            SenderAddress(item),
            (string?)item.Subject ?? string.Empty,
            (DateTime)item.ReceivedTime,
            (int)item.Importance,
            (int)item.FlagStatus,
            (bool)item.UnRead,
            names,
            OutlookComRules.IsProtected(messageClass, names));
    }

    // An Exchange sender carries an X.500 address; its SMTP address is the sender's PR_SMTP_ADDRESS, when Outlook resolves it.
    private static string SenderAddress(dynamic item)
    {
        string address = (string?)item.SenderEmailAddress ?? string.Empty;
        if (!string.Equals((string?)item.SenderEmailType, "EX", StringComparison.OrdinalIgnoreCase))
        {
            return address;
        }

        try
        {
            return (string?)item.PropertyAccessor.GetProperty(SmtpAddressProperty) is { Length: > 0 } smtp ? smtp : address;
        }
        catch (COMException)
        {
            return address;
        }
    }

    private T Invoke<T>(Func<dynamic, T> work)
    {
        T result = default!;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            object? app = null;
            try
            {
                app = createApplication();
                result = work(app);
            }
            catch (OutlookUnavailableException ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            catch (Exception ex)
            {
                // Nothing may escape this thread: an unhandled exception on it ends the whole process. Access denied
                // (UnauthorizedAccessException), any COM error and any other failure become "Outlook not available" (exit 6).
                failure = ExceptionDispatchInfo.Capture(new OutlookUnavailableException(Reason(ex), ex));
            }
            finally
            {
                Release(app);
            }
        })
        {
            IsBackground = true,
            Name = "zyggy-outlook-com",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(callTimeout))
        {
            throw new OutlookPromptTimeoutException(callTimeout);
        }

        failure?.Throw();
        return result;
    }

    private static void Release(object? app)
    {
        if (app is null || !Marshal.IsComObject(app))
        {
            return;
        }

        try
        {
            Marshal.FinalReleaseComObject(app);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidComObjectException)
        {
            // Already released or not a runtime callable wrapper: nothing left to free.
        }
    }

    private static object CreateOutlook()
    {
        Type type = Type.GetTypeFromProgID(ProgId) ?? throw new OutlookUnavailableException("Outlook is not installed");
        return Activator.CreateInstance(type) ?? throw new OutlookUnavailableException("Outlook could not be started");
    }

    private static string Reason(Exception ex) => ex switch
    {
        COMException com => string.Create(CultureInfo.InvariantCulture, $"COM error 0x{com.HResult:X8}: {com.Message}"),
        _ => ex.GetType().Name + ": " + ex.Message,
    };
}
