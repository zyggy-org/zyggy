namespace Zyggy.Core.Outlook;

/// <summary>
/// Whether the run an outlook action comes from is a session the owner runs himself (spec 58 AC-11). A run the node starts carries
/// <c>ZYGGY_HOOKS=off</c> and <c>ZYGGY_JOB</c> (set last by the job runner, so no configured value can undo them); every other
/// unattended run carries <c>ZYGGY_HOOKS=off</c>. Either makes the run unattended, whatever the job's allowed tools say.
/// </summary>
/// <param name="Attended">Whether the run is an attended session.</param>
internal sealed record OutlookRunContext(bool Attended)
{
    /// <summary>Reads the run's environment.</summary>
    /// <param name="environment">The process environment.</param>
    /// <returns>The context.</returns>
    public static OutlookRunContext FromEnvironment(IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var hooksOff = environment.TryGetValue("ZYGGY_HOOKS", out var hooks) && hooks == "off";
        var inJob = environment.TryGetValue("ZYGGY_JOB", out var job) && !string.IsNullOrEmpty(job);
        return new OutlookRunContext(!hooksOff && !inJob);
    }
}
