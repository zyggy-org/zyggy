using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Bus;

/// <summary>The verdict on one inbox file (founding spec §4 "Signature": verify before anything else). Closed.</summary>
public abstract record JobIntake
{
    private protected JobIntake(BusFile file) => File = file;

    /// <summary>Gets the inbox file.</summary>
    public BusFile File { get; }
}

/// <summary>The file is a verified job addressed to this machine.</summary>
public sealed record AcceptedJob : JobIntake
{
    /// <summary>Creates an accepted intake.</summary>
    /// <param name="file">The inbox file.</param>
    /// <param name="job">The verified envelope.</param>
    public AcceptedJob(BusFile file, Envelope.Envelope job)
        : base(file)
    {
        ArgumentNullException.ThrowIfNull(file);
        Job = job ?? throw new ArgumentNullException(nameof(job));
    }

    /// <summary>Gets the verified job envelope.</summary>
    public Envelope.Envelope Job { get; }
}

/// <summary>The file is refused; it moves to <c>jobs/rejected/</c> with a <c>status: rejected</c> report when the sender could be read.</summary>
public sealed record RejectedJob : JobIntake
{
    /// <summary>Creates a rejected intake.</summary>
    /// <param name="file">The inbox file.</param>
    /// <param name="reason">The §4 rejection reason.</param>
    /// <param name="field">The field at fault, when one is.</param>
    /// <param name="detail">A short constant detail; never a field value.</param>
    /// <param name="from">The sender read leniently from the raw front matter; <see langword="null"/> when unreadable (then no report is written).</param>
    public RejectedJob(BusFile file, EnvelopeRejectionReason reason, string? field, string detail, MachineName? from)
        : base(file)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(detail);
        Reason = reason;
        Field = field;
        Detail = detail;
        From = from;
    }

    /// <summary>Gets the rejection reason.</summary>
    public EnvelopeRejectionReason Reason { get; }

    /// <summary>Gets the field at fault, when one is.</summary>
    public string? Field { get; }

    /// <summary>Gets the short constant detail.</summary>
    public string Detail { get; }

    /// <summary>Gets the sender, when it could be read.</summary>
    public MachineName? From { get; }
}
