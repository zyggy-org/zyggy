using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Configuration;

/// <summary>How much a session's status carries (<c>supervision.detail</c>, deliverable 18).</summary>
public enum SupervisionDetail
{
    /// <summary><c>full</c>: project, session, times, and branch, head, dirty count, spec and step.</summary>
    Full,

    /// <summary><c>fixed</c>: project, session and the two times only; the work-laptop setting (founding spec §8).</summary>
    Fixed,
}

/// <summary>
/// The <c>supervision</c> section of <c>node.json</c> (deliverable 18): whether this machine publishes the state of its live sessions,
/// how often, and how much a status carries. The defaults apply when the section is absent. Never a secret.
/// </summary>
/// <param name="Enabled"><see langword="false"/>: no status is written.</param>
/// <param name="StatusMinutes">The least number of minutes between two statuses of one session at a turn end, 1–120.</param>
/// <param name="Detail">
/// How much a status carries. When <c>detail</c> is absent it is <see cref="SupervisionDetail.Fixed"/> on a machine whose
/// <c>discovery.detail</c> is <c>names</c> and <see cref="SupervisionDetail.Full"/> otherwise.
/// </param>
public sealed record SupervisionSettings(bool Enabled = true, int StatusMinutes = 10, SupervisionDetail Detail = SupervisionDetail.Full)
{
    /// <summary>
    /// Gets <c>supervision.workMachines</c>: the machines whose statuses are listed with the four fixed fields only, whatever their
    /// files carry (founding spec §8). Empty when absent.
    /// </summary>
    public IReadOnlyList<MachineName> WorkMachines { get; init; } = Array.Empty<MachineName>();
}
