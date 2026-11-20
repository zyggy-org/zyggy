using System.Globalization;
using System.Text.Json;

using Microsoft.Extensions.Logging;

namespace Zyggy.Core.Supervision;

/// <summary>
/// Tells the owner once per change of the waiting list (deliverable 18, "Waiting list", Mail). A mail goes out when an item exists that
/// no mail has named and the last mail is 30 minutes old or more; an item that arrives inside the window is named when the window
/// ends. What was named, when, and the length of the failure streak are kept in a small file in the node directory, written whole
/// through a temporary file; a lost or corrupt file means one more mail, never a failure. The notice is built from item ids, kinds and
/// names only. A failed send leaves the names unadvanced, is tried again a minute later and is logged once per streak. Never throws
/// to the caller except on cancellation.
/// </summary>
internal sealed partial class OwnerNotifier
{
    /// <summary>The time between two mails.</summary>
    internal static readonly TimeSpan Window = TimeSpan.FromMinutes(30);

    /// <summary>The wait before a failed send is tried again.</summary>
    internal static readonly TimeSpan Retry = TimeSpan.FromMinutes(1);

    private readonly INotifier _notifier;

    private readonly TimeProvider _clock;

    private readonly string _stateFile;

    private readonly ILogger<OwnerNotifier> _logger;

    private OwnerMailState? _state;

    private DateTimeOffset _retryAt = DateTimeOffset.MinValue;

    /// <summary>Initializes a new instance of the <see cref="OwnerNotifier"/> class.</summary>
    /// <param name="notifier">The channel to the owner.</param>
    /// <param name="clock">The clock.</param>
    /// <param name="stateFile">The file that keeps what was named, in the node directory.</param>
    /// <param name="logger">The logger.</param>
    public OwnerNotifier(INotifier notifier, TimeProvider clock, string stateFile, ILogger<OwnerNotifier> logger)
    {
        _notifier = notifier;
        _clock = clock;
        _stateFile = stateFile;
        _logger = logger;
    }

    /// <summary>Takes the waiting list as it stands now and mails the owner when it has changed and the window allows.</summary>
    /// <param name="items">The waiting list.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the list was handled.</returns>
    public async Task RunAsync(IReadOnlyList<OwnerItem> items, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(items);
        DateTimeOffset now = _clock.GetUtcNow();
        OwnerMailState state = _state ??= Load();
        HashSet<string> present = [.. items.Select(i => i.Key)];
        if (state.Named.RemoveAll(k => !present.Contains(k)) > 0)
        {
            Save(state);
        }

        OwnerItem[] unnamed = [.. items.Where(i => !state.Named.Contains(i.Key))];
        if (unnamed.Length == 0 || now < _retryAt || (state.LastMail is { } last && now < last + Window))
        {
            return;
        }

        try
        {
            await _notifier.NotifyAsync(Notice(unnamed, now), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _retryAt = now + Retry;
            state.Failures++;
            if (state.Failures == 1)
            {
                LogSendFailed(ex.Message);
            }

            Save(state);
            return;
        }

        state.Named.AddRange(unnamed.Select(i => i.Key));
        state.LastMail = now;
        state.Failures = 0;
        Save(state);
    }

    private static OwnerNotice Notice(OwnerItem[] items, DateTimeOffset now) =>
        new(
            "Zyggy: " + items.Length.ToString(CultureInfo.InvariantCulture) + " item(s) wait for you",
            [.. items.Select(i => string.Join(", ", new[] { i.Kind.ToString().ToLowerInvariant(), i.Machine.Value, i.Project, i.Id.Value, Age(now - i.Since) }.OfType<string>()))]);

    private static string Age(TimeSpan age) =>
        age < TimeSpan.FromHours(1) ? Math.Max(0, (int)age.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " min"
        : age < TimeSpan.FromHours(48) ? ((int)age.TotalHours).ToString(CultureInfo.InvariantCulture) + " h"
        : ((int)age.TotalDays).ToString(CultureInfo.InvariantCulture) + " d";

    private OwnerMailState Load()
    {
        try
        {
            return JsonSerializer.Deserialize<OwnerMailState>(File.ReadAllText(_stateFile)) is { Named: { } named } loaded
                ? loaded with { Named = [.. named] }
                : new OwnerMailState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new OwnerMailState();
        }
    }

    private void Save(OwnerMailState state)
    {
        string temporary = _stateFile + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(state));
            File.Move(temporary, _stateFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A state that cannot be kept costs one more mail after a restart, nothing else.
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "owner mail: send failed, tried again in a minute: {Detail}")]
    private partial void LogSendFailed(string detail);

    private sealed record OwnerMailState
    {
        public List<string> Named { get; set; } = [];

        public DateTimeOffset? LastMail { get; set; }

        public int Failures { get; set; }
    }
}
