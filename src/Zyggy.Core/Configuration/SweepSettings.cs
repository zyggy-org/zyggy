namespace Zyggy.Core.Configuration;

/// <summary>
/// The <c>sweep</c> section of <c>node.json</c> (spec 17; founding spec §10 as amended by deliverable 17): how often the node service on
/// Central runs the sweep. Read only on Central; the default applies when the section is absent. Never a secret.
/// </summary>
/// <param name="IntervalMinutes">Minutes between two sweep passes of the node service, 1–1440.</param>
public sealed record SweepSettings(int IntervalMinutes = 60);
