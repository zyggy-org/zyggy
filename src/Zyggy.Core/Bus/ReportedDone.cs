using System.Globalization;

using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Bus;

/// <summary>One remembered job id: when it was noted and the <see cref="JobFingerprint"/> of the job it belongs to.</summary>
/// <param name="At">The instant it was noted.</param>
/// <param name="Fingerprint">The job's fingerprint.</param>
internal sealed record JobMark(DateTimeOffset At, string Fingerprint);

/// <summary>
/// Spec 61: job ids remembered in the node folder, one file per id under <c>&lt;nodeDir&gt;/work/&lt;tenant&gt;/&lt;folder&gt;/&lt;job id&gt;</c>,
/// each holding two lines: the UTC instant it was noted and the job's <see cref="JobFingerprint"/>. One file per id, so runs ending at the
/// same time never write the same file. A record older than 14 days is forgotten; a record that cannot be read, has no fingerprint or is
/// dated after the instant it is read at is invalid: it never counts and is removed (the caller logs it once).
/// </summary>
internal abstract class JobMarks
{
    /// <summary>How long an id is remembered.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(14);

    private readonly string _directory;

    /// <summary>Creates the store for one tenant in <paramref name="nodeDir"/>.</summary>
    /// <param name="nodeDir">The node directory.</param>
    /// <param name="tenant">This machine's tenant.</param>
    /// <param name="folder">The store's folder name.</param>
    protected JobMarks(string nodeDir, TenantId tenant, string folder)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeDir);
        ArgumentNullException.ThrowIfNull(tenant);
        Name = folder;
        _directory = Path.Join(nodeDir, "work", tenant.Value, folder);
    }

    /// <summary>Gets the store's folder name, as log lines name it.</summary>
    public string Name { get; }

    /// <summary>
    /// Remembers <paramref name="job"/> with <paramref name="fingerprint"/> at <paramref name="at"/>, and forgets the records older than
    /// <see cref="Window"/>; returns the invalid records removed on the way (file name and why), for the caller to log.
    /// </summary>
    /// <param name="job">The job id.</param>
    /// <param name="at">The instant.</param>
    /// <param name="fingerprint">The job's fingerprint.</param>
    /// <returns>The invalid records removed.</returns>
    public IReadOnlyList<(string Name, string Why)> Note(EnvelopeId job, DateTimeOffset at, string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentException.ThrowIfNullOrEmpty(fingerprint);
        Directory.CreateDirectory(_directory);
        var invalid = new List<(string Name, string Why)>();
        foreach (string file in Directory.EnumerateFiles(_directory))
        {
            (JobMark? mark, string? why) = Read(file, at);
            if (why is not null)
            {
                invalid.Add((Path.GetFileName(file), why));
                File.Delete(file);
            }
            else if (at - mark!.At >= Window)
            {
                File.Delete(file);
            }
        }

        File.WriteAllText(Path.Join(_directory, job.Value), at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) + "\n" + fingerprint + "\n");
        return invalid;
    }

    /// <summary>
    /// The record of <paramref name="job"/> when it was noted less than <see cref="Window"/> before <paramref name="now"/>; otherwise
    /// <see langword="null"/>. An invalid record is removed and <paramref name="invalid"/> says why.
    /// </summary>
    /// <param name="job">The job id.</param>
    /// <param name="now">The current instant.</param>
    /// <param name="invalid">Why the record was invalid and removed; <see langword="null"/> when it was not.</param>
    /// <returns>The record, or <see langword="null"/>.</returns>
    public JobMark? Find(EnvelopeId job, DateTimeOffset now, out string? invalid)
    {
        ArgumentNullException.ThrowIfNull(job);
        invalid = null;
        string file = Path.Join(_directory, job.Value);
        if (!File.Exists(file))
        {
            return null;
        }

        (JobMark? mark, string? why) = Read(file, now);
        if (why is not null)
        {
            invalid = why;
            File.Delete(file);
            return null;
        }

        return now - mark!.At < Window ? mark : null;
    }

    /// <summary>Forgets <paramref name="job"/>; nothing happens when it is not remembered.</summary>
    /// <param name="job">The job id.</param>
    public void Forget(EnvelopeId job)
    {
        ArgumentNullException.ThrowIfNull(job);
        string file = Path.Join(_directory, job.Value);
        if (File.Exists(file))
        {
            File.Delete(file);
        }
    }

    private static (JobMark? Mark, string? Why) Read(string file, DateTimeOffset now)
    {
        string[] lines = File.ReadAllText(file).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length != 2
            || !DateTimeOffset.TryParse(lines[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset at))
        {
            return (null, "unreadable");
        }

        if (lines[1].Length != 64 || !lines[1].All(char.IsAsciiHexDigitLower))
        {
            return (null, "no fingerprint");
        }

        return at > now ? (null, "dated in the future") : (new JobMark(at, lines[1]), null);
    }
}

/// <summary>
/// Spec 61 "Never a second run": the ids of the jobs this node finished <c>done</c>, remembered for 14 days (the archive age) under
/// <c>&lt;nodeDir&gt;/work/&lt;tenant&gt;/reported-done/</c>, noted once the run's signed result is kept in the node folder.
/// </summary>
internal sealed class ReportedDone : JobMarks
{
    /// <summary>Creates the store for one tenant in <paramref name="nodeDir"/>.</summary>
    /// <param name="nodeDir">The node directory.</param>
    /// <param name="tenant">This machine's tenant.</param>
    public ReportedDone(string nodeDir, TenantId tenant)
        : base(nodeDir, tenant, "reported-done")
    {
    }
}

/// <summary>
/// Spec 61 "Never a second run": the ids of the jobs this node started a run of, under <c>&lt;nodeDir&gt;/work/&lt;tenant&gt;/running/</c>, from
/// the run's start until its report is on the remote, so a different envelope under such an id is refused also after a restart.
/// </summary>
internal sealed class RunningJobs : JobMarks
{
    /// <summary>Creates the store for one tenant in <paramref name="nodeDir"/>.</summary>
    /// <param name="nodeDir">The node directory.</param>
    /// <param name="tenant">This machine's tenant.</param>
    public RunningJobs(string nodeDir, TenantId tenant)
        : base(nodeDir, tenant, "running")
    {
    }
}
