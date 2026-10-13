namespace Zyggy.Core.Bus;

/// <summary>The result of applying one <see cref="HeadProbe"/> to a <see cref="PollState"/>.</summary>
/// <param name="State">The next state.</param>
/// <param name="Pull">Whether the host should pull and process the bus now (the branch moved).</param>
/// <param name="PhaseChanged">The new phase when it differs from the previous one — the host logs exactly then (§11); otherwise <see langword="null"/>.</param>
public sealed record PollTransition(PollState State, bool Pull, PollPhase? PhaseChanged);
