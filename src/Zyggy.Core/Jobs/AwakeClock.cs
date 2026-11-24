using System.Globalization;

namespace Zyggy.Core.Jobs;

/// <summary>Which limit ended a run on the <see cref="AwakeClock"/>.</summary>
internal enum AwakeLimit
{
    /// <summary>The run's awake time reached its <c>timeout_minutes</c>.</summary>
    AwakeTime,

    /// <summary>The run's wall-clock age reached <see cref="AwakeClock.WallLimit"/>.</summary>
    WallClock,
}

/// <summary>
/// A run's time limit counted in awake time (spec 61 Behavior §3, founding spec §6 as reshaped): while the run is alive the clock is
/// noted at a short interval; a gap of more than <see cref="SleepGap"/> between two notes is time asleep and does not count, a backward
/// jump counts as zero. <see cref="Reached"/> is cancelled when the awake time reaches the limit, or when the wall-clock age since the
/// run's start reaches <see cref="WallLimit"/> whatever the awake time. Time comes from the injected <see cref="TimeProvider"/> only.
/// </summary>
internal sealed class AwakeClock : IDisposable
{
    /// <summary>How often the clock is noted while the run is alive.</summary>
    public static readonly TimeSpan NoteInterval = TimeSpan.FromSeconds(30);

    /// <summary>A gap between two notes longer than this is time asleep.</summary>
    public static readonly TimeSpan SleepGap = TimeSpan.FromMinutes(5);

    /// <summary>The wall-clock age at which a run ends whatever its awake time (spec 61 Q2: a fixed 24 hours).</summary>
    public static readonly TimeSpan WallLimit = TimeSpan.FromHours(24);

    private readonly TimeProvider _clock;
    private readonly TimeSpan _limit;
    private readonly DateTimeOffset _started;
    private readonly CancellationTokenSource _reached = new();
    private readonly ITimer _timer;
    private readonly Lock _gate = new();
    private DateTimeOffset _last;
    private bool _disposed;

    /// <summary>Starts counting now.</summary>
    /// <param name="clock">The clock.</param>
    /// <param name="limit">The awake time the run may use.</param>
    /// <param name="started">The run's start, the origin of its wall-clock age.</param>
    public AwakeClock(TimeProvider clock, TimeSpan limit, DateTimeOffset started)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(limit, TimeSpan.Zero);
        _limit = limit;
        _started = started;
        _last = clock.GetUtcNow();
        _timer = clock.CreateTimer(_ => Note(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Schedule();
    }

    /// <summary>Gets the awake time counted so far.</summary>
    public TimeSpan Awake { get; private set; }

    /// <summary>Gets the time asleep counted so far.</summary>
    public TimeSpan Asleep { get; private set; }

    /// <summary>Gets the limit that ended the run, or <see langword="null"/> while none has.</summary>
    public AwakeLimit? Ended { get; private set; }

    /// <summary>Gets a token cancelled once a limit is reached.</summary>
    public CancellationToken Reached => _reached.Token;

    /// <summary>The <c>asleep &lt;h&gt;h&lt;mm&gt;</c> text of <paramref name="asleep"/>.</summary>
    /// <param name="asleep">The time asleep.</param>
    /// <returns>Hours, <c>h</c>, two-digit minutes, rounded down.</returns>
    public static string Format(TimeSpan asleep) =>
        string.Create(CultureInfo.InvariantCulture, $"{(long)asleep.TotalHours}h{asleep.Minutes:00}");

    /// <summary>Notes the clock: counts the gap since the last note as awake or asleep and checks both limits.</summary>
    public void Note()
    {
        var reached = false;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var now = _clock.GetUtcNow();
            var gap = now - _last;
            if (gap > SleepGap)
            {
                Asleep += gap;
            }
            else if (gap > TimeSpan.Zero)
            {
                Awake += gap;
            }

            _last = now;
            if (Ended is null)
            {
                if (now - _started >= WallLimit)
                {
                    Ended = AwakeLimit.WallClock;
                }
                else if (Awake >= _limit)
                {
                    Ended = AwakeLimit.AwakeTime;
                }

                reached = Ended is not null;
            }

            if (Ended is null)
            {
                Schedule();
            }
        }

        if (reached)
        {
            try
            {
                _reached.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The run ended and disposed the clock between the note and the signal: nothing is left to stop.
            }
        }
    }

    /// <summary>Takes a last note (a sleep since the last one still counts) and stops noting.</summary>
    public void Stop()
    {
        Note();
        lock (_gate)
        {
            _disposed = true;
        }

        _timer.Dispose();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        _timer.Dispose();
        _reached.Dispose();
    }

    // The next note at the interval, or sooner when less awake time than that is left, so the run ends at its limit.
    private void Schedule()
    {
        var left = _limit - Awake;
        _timer.Change(left < NoteInterval ? left : NoteInterval, Timeout.InfiniteTimeSpan);
    }
}
