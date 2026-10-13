namespace Zyggy.Core.Bus.Probe;

/// <summary>One row of the reachability matrix (deliverable 04): which check, against what, how it ended, the redacted evidence, and the proxy in the way.</summary>
/// <param name="Check">The check: <c>api</c>, <c>fetch</c>, <c>push</c> or <c>ssh</c>.</param>
/// <param name="Endpoint">The host or URL checked, without credentials.</param>
/// <param name="Status">The classification.</param>
/// <param name="Detail">The redacted raw evidence (git's stderr, the probe outcome), so a misclassified line is still evidence.</param>
/// <param name="Proxy">The proxy used, as <c>host:port</c>, or <c>none</c>.</param>
public sealed record ReachResult(string Check, string Endpoint, ReachStatus Status, string Detail, string Proxy);
