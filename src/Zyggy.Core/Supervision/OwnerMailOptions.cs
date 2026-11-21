namespace Zyggy.Core.Supervision;

/// <summary>
/// The owner mail's timings on Central's node (deliverable 18, "Mail"): how often the waiting list is looked at between pickup passes,
/// the window between two mails and the wait before a failed send is tried again. The defaults are the spec's; tests shorten them.
/// </summary>
internal sealed class OwnerMailOptions
{
    /// <summary>Gets or sets the period of the owner pass that runs without a pull (an item named when its window ends, a retry).</summary>
    public TimeSpan Tick { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Gets or sets the time between two mails.</summary>
    public TimeSpan Window { get; set; } = OwnerNotifier.Window;

    /// <summary>Gets or sets the wait before a failed send is tried again.</summary>
    public TimeSpan Retry { get; set; } = OwnerNotifier.Retry;
}

/// <summary>Where Central's node hands its decisions to the morning brief (deliverable 18, "Record"); registered only with an M365 instance.</summary>
/// <param name="DecidedFile">The brief directory's <c>decided.json</c>.</param>
internal sealed record OwnerViewPaths(string DecidedFile);
