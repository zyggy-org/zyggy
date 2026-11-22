using System.Text;
using System.Text.Json;

using Zyggy.Core.Envelope;
using Zyggy.Core.Memory;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Outlook;

/// <summary>
/// The work mail read tools in a run the node started (spec 58 slice 5, AC-16, AC-9; D-Q6, D-Q14, D-Q17). A job run is known by a
/// non-empty <c>ZYGGY_JOB</c>, which the job runner sets last. In a job run the read tools print only mail that passes the
/// sensitive-data filter and record, in a marker on the laptop (<c>&lt;nodeDir&gt;/work/&lt;tenant&gt;/outlook/jobs/&lt;job id&gt;.json</c>),
/// that the job read work mail and which mails stayed on the laptop. The job runner screens the job's report while the marker exists
/// (<see cref="WorkMailReportScreen"/>), and the <c>zyggy</c> entry point refuses every verb but <c>outlook</c> for that job
/// (<see cref="GateRefusal"/>). The job brief keeps its own watermark (<c>outlook/job/brief.json</c>), so an attended brief still
/// shows every mail, the kept ones included.
/// </summary>
internal static class OutlookJobRun
{
    /// <summary>The environment variable the job runner sets to the job's id.</summary>
    public const string JobVariable = "ZYGGY_JOB";

    /// <summary>
    /// The environment variable the job runner sets, last, to the job's marker path in its own node directory. The read tools refuse a
    /// job whose <c>node.json</c> would put the marker anywhere else, so the runner always finds it and screens the report.
    /// </summary>
    public const string MarkerVariable = "ZYGGY_JOB_MARKER";

    // The verbs a job that read work mail may still run: the outlook tools (share runs the filter itself), the job's own tool hook,
    // and the root's version and help.
    private static readonly HashSet<string> GateAllowed = new(["outlook", "hook", "--version", "--help", "-h", "-?"], StringComparer.Ordinal);

    /// <summary>Whether the run is a job the node started.</summary>
    /// <param name="environment">The process environment.</param>
    /// <param name="value">The raw <c>ZYGGY_JOB</c> value.</param>
    /// <returns>Whether <c>ZYGGY_JOB</c> is set and not empty.</returns>
    public static bool IsJob(IReadOnlyDictionary<string, string?> environment, out string value)
    {
        ArgumentNullException.ThrowIfNull(environment);
        value = environment.TryGetValue(JobVariable, out string? job) && !string.IsNullOrEmpty(job) ? job : string.Empty;
        return value.Length > 0;
    }

    /// <summary>Returns the marker of a job that read work mail.</summary>
    /// <param name="nodeDir">The node directory.</param>
    /// <param name="tenant">The tenant.</param>
    /// <param name="job">The job's id.</param>
    /// <returns>The path.</returns>
    public static string MarkerPath(string nodeDir, TenantId tenant, EnvelopeId job)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeDir);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(job);
        return Path.Join(nodeDir, "work", tenant.Value, "outlook", "jobs", job.Value + ".json");
    }

    /// <summary>Returns the brief state of job-run briefs: its own watermark and backlog, beside the attended one.</summary>
    /// <param name="nodeDir">The node directory.</param>
    /// <param name="tenant">The tenant.</param>
    /// <returns>The path.</returns>
    public static string JobStatePath(string nodeDir, TenantId tenant)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeDir);
        ArgumentNullException.ThrowIfNull(tenant);
        return Path.Join(nodeDir, "work", tenant.Value, "outlook", "job", OutlookDrafts.StateFileName);
    }

    /// <summary>Records that the job read work mail and which mails stayed on the laptop; a reference is counted once per job.</summary>
    /// <param name="markerPath">The job's marker.</param>
    /// <param name="kept">The references of the mails kept on the laptop by this call.</param>
    public static void Record(string markerPath, IEnumerable<string> kept)
    {
        ArgumentException.ThrowIfNullOrEmpty(markerPath);
        ArgumentNullException.ThrowIfNull(kept);
        var all = new SortedSet<string>(Kept(markerPath), StringComparer.Ordinal);
        all.UnionWith(kept);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("kept");
            foreach (string reference in all)
            {
                writer.WriteStringValue(reference);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
        MemoryFileWriter.WriteAtomically(markerPath, Encoding.UTF8.GetString(buffer.ToArray()) + "\n");
    }

    /// <summary>The number of distinct mails the job kept on the laptop; 0 without a marker.</summary>
    /// <param name="markerPath">The job's marker.</param>
    /// <returns>The count.</returns>
    public static int KeptCount(string markerPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(markerPath);
        return Kept(markerPath).Count;
    }

    /// <summary>
    /// The entry point's gate: in a job whose marker exists, every verb but <c>outlook</c>, the job hook and <c>--version</c>/<c>--help</c>
    /// is refused, so nothing the job read can leave through another verb (<c>context</c>, <c>ask</c>, <c>submit</c>, the memory verbs, …).
    /// The marker is the one the runner named (<see cref="MarkerVariable"/>); outside a job, or before the job's first read, it allows
    /// (the read tools refuse a job without that variable, so no read can have run).
    /// </summary>
    /// <param name="args">The <c>zyggy</c> arguments.</param>
    /// <param name="environment">The process environment.</param>
    /// <returns>The refusal line, or <see langword="null"/> when the verb may run.</returns>
    public static string? GateRefusal(IReadOnlyList<string> args, IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(environment);
        if (!IsJob(environment, out _) || (args.Count > 0 && GateAllowed.Contains(args[0]))
            || !environment.TryGetValue(MarkerVariable, out string? marker) || string.IsNullOrEmpty(marker) || !File.Exists(marker))
        {
            return null;
        }

        return "zyggy: refused: this job read work mail; until it ends only zyggy outlook runs (spec 58, the sensitive-data filter)";
    }

    /// <summary>Whether the marker the runner named is this node's marker for the job (same file, the OS's path rules).</summary>
    /// <param name="named">The runner's <see cref="MarkerVariable"/>, or <see langword="null"/>.</param>
    /// <param name="own">The marker path from this <c>node.json</c>.</param>
    /// <returns>Whether they name the same file.</returns>
    public static bool IsOwnMarker(string? named, string own)
    {
        ArgumentException.ThrowIfNullOrEmpty(own);
        return !string.IsNullOrEmpty(named)
            && string.Equals(Path.GetFullPath(named), Path.GetFullPath(own), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static List<string> Kept(string markerPath)
    {
        if (!File.Exists(markerPath))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(markerPath));
            return document.RootElement.TryGetProperty("kept", out JsonElement kept) && kept.ValueKind == JsonValueKind.Array
                ? [.. kept.EnumerateArray().Where(k => k.ValueKind == JsonValueKind.String).Select(k => k.GetString()!)]
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
