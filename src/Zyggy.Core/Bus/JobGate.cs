using Zyggy.Core.Jobs;

namespace Zyggy.Core.Bus;

/// <summary>
/// Consulted by the listener for a verified, addressed job <em>before</em> the claim (founding spec §6 step 3: "the dispatcher leaves the
/// job in <c>jobs/</c>"): <see cref="Admitted"/> claims and runs it, <see cref="Deferred"/> leaves the file untouched for a later tick.
/// Rejected files never reach the gate. The Node registers the <see cref="JobDispatcher"/>'s rules; the probe verbs admit everything.
/// </summary>
/// <param name="job">The job envelope.</param>
/// <param name="ct">Cancellation token.</param>
/// <returns>The decision.</returns>
public delegate Task<AdmissionDecision> JobGate(Envelope.Envelope job, CancellationToken ct);
