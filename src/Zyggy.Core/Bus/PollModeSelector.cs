namespace Zyggy.Core.Bus;

/// <summary>
/// Which <see cref="PollMode"/> applies at <paramref name="nowUtc"/> (founding spec §5 item 3): the Node answers from its work hours
/// (<see cref="Policy.WorkHours.ModeSelector"/>); the probe verbs answer <see cref="PollMode.Active"/>. Re-evaluated by the listener every tick.
/// </summary>
/// <param name="nowUtc">The current instant.</param>
/// <returns>The mode.</returns>
public delegate PollMode PollModeSelector(DateTimeOffset nowUtc);
