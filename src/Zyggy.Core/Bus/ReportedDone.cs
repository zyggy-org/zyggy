using System.Globalization;

using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Bus;

/// <summary>
/// Spec 61 "Never a second run": the ids of the jobs this node finished <c>done</c>, remembered for 14 days (the archive age) in the node
/// folder under <c>&lt;nodeDir&gt;/work/&lt;tenant&gt;/reported-done/&lt;job id&gt;</c>, each file holding the UTC instant the result was kept.
/// One file per id, so runs ending at the same time never write the same file.
/// </summary>
internal sealed class ReportedDone
{
    /// <summary>How long an id is remembered.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(14);

    private readonly string _directory;

    /// <summary>Creates the store for one tenant in <paramref name="nodeDir"/>.</summary>
    /// <param name="nodeDir">The node directory.</param>
    /// <param name="tenant">This machine's tenant.</param>
    public ReportedDone(string nodeDir, TenantId tenant)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeDir);
        ArgumentNullException.ThrowIfNull(tenant);
        _directory = Path.Join(nodeDir, "work", tenant.Value, "reported-done");
    }

    /// <summary>Remembers that <paramref name="job"/> ended <c>done</c> at <paramref name="at"/>, and forgets the ids older than <see cref="Window"/>.</summary>
    /// <param name="job">The job id.</param>
    /// <param name="at">The instant.</param>
    public void Note(EnvelopeId job, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(job);
        Directory.CreateDirectory(_directory);
        foreach (string file in Directory.EnumerateFiles(_directory))
        {
            if (ReadAt(file) is not { } noted || at - noted >= Window)
            {
                File.Delete(file);
            }
        }

        File.WriteAllText(Path.Join(_directory, job.Value), at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
    }

    /// <summary>When <paramref name="job"/> ended <c>done</c> less than <see cref="Window"/> before <paramref name="now"/>, that instant; otherwise <see langword="null"/>.</summary>
    /// <param name="job">The job id.</param>
    /// <param name="now">The current instant.</param>
    /// <returns>The instant it was noted, or <see langword="null"/>.</returns>
    public DateTimeOffset? DoneWithin(EnvelopeId job, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(job);
        string file = Path.Join(_directory, job.Value);
        return File.Exists(file) && ReadAt(file) is { } noted && now - noted < Window ? noted : null;
    }

    private static DateTimeOffset? ReadAt(string file) =>
        DateTimeOffset.TryParse(File.ReadAllText(file).Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset at) ? at : null;
}
