namespace Zyggy.Core.Supervision;

/// <summary>
/// The hourly cap on supervisor runs (<c>supervision.runsPerHour</c>, deliverable 18): at most that many starts within any 60
/// minutes. Kept in memory; a restart starts it at zero.
/// </summary>
/// <param name="perHour">The cap.</param>
internal sealed class SupervisorRunBudget(int perHour)
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(60);

    private readonly Queue<DateTimeOffset> _starts = new();

    /// <summary>Gets the cap.</summary>
    public int PerHour { get; } = perHour;

    /// <summary>
    /// Counts a start at <paramref name="now"/> when fewer than the cap started within the last 60 minutes; a start exactly 60
    /// minutes old no longer counts, and a refused start is not counted.
    /// </summary>
    public bool TryStart(DateTimeOffset now)
    {
        while (_starts.Count > 0 && now - _starts.Peek() >= Window)
        {
            _starts.Dequeue();
        }

        if (_starts.Count >= PerHour)
        {
            return false;
        }

        _starts.Enqueue(now);
        return true;
    }
}
