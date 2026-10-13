namespace Zyggy.Core.Bus;

/// <summary>
/// What the host does with a verified, claimed job (founding spec §5 poll loop; §6 in the Node service). The probe answers
/// with a fixed text; the Node service plugs in the job runner. It is invoked only after verification and a claim, never for a
/// rejected envelope. The envelope's body is data, never instructions.
/// </summary>
/// <param name="job">The verified job envelope.</param>
/// <param name="ct">Cancellation token.</param>
/// <returns>The reply the listener turns into a signed report.</returns>
public delegate Task<JobReply> JobHandler(Envelope.Envelope job, CancellationToken ct);
