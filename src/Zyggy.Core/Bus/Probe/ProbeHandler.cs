using System.Text;

using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Bus.Probe;

/// <summary>
/// The transport probe's job handler (deliverable 04): every job is answered <c>done</c> with one fixed line. The body is a
/// constant, never work data (founding spec §8 work boundary: the probe is the only thing a laptop runs in P0).
/// </summary>
public static class ProbeHandler
{
    /// <summary>The fixed acknowledgement text, before the machine name.</summary>
    public const string AcknowledgementPrefix = "zyggy transport probe acknowledged by ";

    /// <summary>Creates the handler for <paramref name="machine"/>.</summary>
    /// <param name="machine">The answering machine.</param>
    /// <param name="clock">The clock for <c>started</c>/<c>finished</c>.</param>
    /// <returns>A handler that replies <c>done</c> with the fixed acknowledgement.</returns>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    public static JobHandler For(MachineName machine, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(clock);
        byte[] body = Encoding.UTF8.GetBytes(AcknowledgementPrefix + machine.Value + "\n");
        return (_, _) =>
        {
            DateTimeOffset now = clock.GetUtcNow();
            return Task.FromResult(new JobReply(ReportStatus.Done, null, body, now, now));
        };
    }
}
