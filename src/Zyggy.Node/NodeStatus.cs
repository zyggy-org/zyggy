using Zyggy.Core.Bus;

namespace Zyggy.Node;

/// <summary>What the host knows about itself for <c>/health</c> (spec 07): the loop's listener once it exists and the login probe's last verdict.</summary>
internal sealed class NodeStatus
{
    private static readonly TimeSpan ReadyPoll = TimeSpan.FromSeconds(1);

    private readonly TaskCompletionSource _listenerStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Gets or sets the loop's listener; <see langword="null"/> until <see cref="PollLoop"/> starts.</summary>
    public BusListener? Listener { get; set; }

    /// <summary>Gets or sets the login probe's last verdict (§11 <c>claudeAuthOk</c>); <see langword="null"/> until the first probe answers.</summary>
    public bool? ClaudeAuthOk { get; set; }

    /// <summary>Marks the listener as started and the health endpoint as up (plan 14 A2); the first registry publish waits for it.</summary>
    public void MarkListenerStarted() => _listenerStarted.TrySetResult();

    /// <summary>
    /// Plan 14 A2: waits until the listener has started, then until it has polled once (checked every second on <paramref name="clock"/>),
    /// at most <paramref name="bound"/> for the second part — so the first registry file never carries the "never polled" red.
    /// A refused start-up never marks the listener started: the wait ends only with <paramref name="ct"/>.
    /// </summary>
    public async Task WhenReadyAsync(TimeProvider clock, TimeSpan bound, CancellationToken ct)
    {
        await _listenerStarted.Task.WaitAsync(ct).ConfigureAwait(false);
        DateTimeOffset deadline = clock.GetUtcNow() + bound;
        while (Listener?.Status.LastPollUtc is null && clock.GetUtcNow() < deadline)
        {
            await Task.Delay(ReadyPoll, clock, ct).ConfigureAwait(false);
        }
    }
}
