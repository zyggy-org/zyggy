using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Bus;

/// <summary>One kept result: the job it answers, the signed report envelope's bytes and the attached file, if any.</summary>
/// <param name="Job">The id of the job the report answers.</param>
/// <param name="Report">The signed report envelope, byte for byte as it goes on the bus.</param>
/// <param name="Attachment">The attached file's bytes (spec 59), or <see langword="null"/>.</param>
internal sealed record KeptReport(EnvelopeId Job, byte[] Report, byte[]? Attachment);

/// <summary>
/// Spec 61 "Kept result": a finished run's signed report, held in the node folder (never on the bus) under
/// <c>&lt;nodeDir&gt;/work/&lt;tenant&gt;/kept-reports/&lt;job id&gt;.md</c> (and <c>&lt;job id&gt;.file</c> for an attached file) from before
/// its bus write until its commit is on the remote. Each file is written to a temporary name and then moved into place.
/// </summary>
internal sealed class KeptReports
{
    private const string ReportExtension = ".md";

    private const string AttachmentExtension = ".file";

    private readonly string _directory;

    /// <summary>Creates the store for one tenant in <paramref name="nodeDir"/>.</summary>
    /// <param name="nodeDir">The node directory.</param>
    /// <param name="tenant">This machine's tenant.</param>
    public KeptReports(string nodeDir, TenantId tenant)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeDir);
        ArgumentNullException.ThrowIfNull(tenant);
        _directory = DirectoryFor(nodeDir, tenant);
    }

    /// <summary>The store's folder in <paramref name="nodeDir"/> for <paramref name="tenant"/>.</summary>
    /// <param name="nodeDir">The node directory.</param>
    /// <param name="tenant">The tenant.</param>
    /// <returns>The folder's path.</returns>
    public static string DirectoryFor(string nodeDir, TenantId tenant)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeDir);
        ArgumentNullException.ThrowIfNull(tenant);
        return Path.Join(nodeDir, "work", tenant.Value, "kept-reports");
    }

    /// <summary>Keeps the signed report of <paramref name="job"/> (and its attached file), replacing an earlier one.</summary>
    /// <param name="job">The job the report answers.</param>
    /// <param name="report">The signed report's bytes.</param>
    /// <param name="attachment">The attached file's bytes, or <see langword="null"/>.</param>
    public void Keep(EnvelopeId job, byte[] report, ReadOnlyMemory<byte>? attachment)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(report);
        Directory.CreateDirectory(_directory);
        if (attachment is { } file)
        {
            WriteAtomically(PathOf(job, AttachmentExtension), file.ToArray());
        }
        else
        {
            File.Delete(PathOf(job, AttachmentExtension));
        }

        WriteAtomically(PathOf(job, ReportExtension), report);
    }

    /// <summary>True when a result of <paramref name="job"/> is kept.</summary>
    /// <param name="job">The job id.</param>
    /// <returns>Whether a kept report exists.</returns>
    public bool Has(EnvelopeId job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return File.Exists(PathOf(job, ReportExtension));
    }

    /// <summary>Removes the kept result of <paramref name="job"/>; nothing happens when there is none.</summary>
    /// <param name="job">The job id.</param>
    public void Remove(EnvelopeId job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (!Directory.Exists(_directory))
        {
            return;
        }

        File.Delete(PathOf(job, ReportExtension));
        File.Delete(PathOf(job, AttachmentExtension));
    }

    /// <summary>
    /// Moves the kept result of <paramref name="job"/> aside into the store's <c>quarantined/</c> folder (spec 61, a kept file that does not
    /// verify): it is never written to the bus and no longer counts as a kept report for its id. An earlier quarantined copy is replaced.
    /// </summary>
    /// <param name="job">The job id.</param>
    /// <returns>The quarantined report file's path.</returns>
    public string Quarantine(EnvelopeId job)
    {
        ArgumentNullException.ThrowIfNull(job);
        string folder = Path.Join(_directory, "quarantined");
        Directory.CreateDirectory(folder);
        string target = Path.Join(folder, job.Value + ReportExtension);
        string attachment = PathOf(job, AttachmentExtension);
        if (File.Exists(attachment))
        {
            File.Move(attachment, Path.Join(folder, job.Value + AttachmentExtension), overwrite: true);
        }

        File.Move(PathOf(job, ReportExtension), target, overwrite: true);
        return target;
    }

    /// <summary>The kept results, oldest job first; files whose name is not a job id are ignored.</summary>
    /// <returns>The kept results.</returns>
    public IReadOnlyList<KeptReport> List()
    {
        if (!Directory.Exists(_directory))
        {
            return [];
        }

        var kept = new List<KeptReport>();
        foreach (string file in Directory.EnumerateFiles(_directory, "*" + ReportExtension).Order(StringComparer.Ordinal))
        {
            if (!EnvelopeId.TryParse(Path.GetFileNameWithoutExtension(file), out EnvelopeId? job))
            {
                continue;
            }

            string attachment = PathOf(job, AttachmentExtension);
            kept.Add(new KeptReport(job, File.ReadAllBytes(file), File.Exists(attachment) ? File.ReadAllBytes(attachment) : null));
        }

        return kept;
    }

    private static void WriteAtomically(string path, byte[] bytes)
    {
        string temporary = path + ".tmp";
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, path, overwrite: true);
    }

    private string PathOf(EnvelopeId job, string extension) => Path.Join(_directory, job.Value + extension);
}
