namespace Zyggy.Core.Jobs;

/// <summary>Why the dispatcher leaves a job in <c>jobs/</c> for now (founding spec §6 Concurrency; spec 07).</summary>
public enum DeferralReason
{
    /// <summary>The machine is outside its working window and the job is not <c>priority: high</c>.</summary>
    OutsideWorkHours,

    /// <summary><c>maxConcurrentJobs</c> runs are already in flight on this machine.</summary>
    NoSlot,

    /// <summary>The job's project is in flight here, or its lock is held by another process.</summary>
    ProjectBusy,
}

/// <summary>The dispatcher's answer for one job: <see cref="Admitted"/> (run it now, holding the lease) or <see cref="Deferred"/> (leave it in <c>jobs/</c>, nothing written). Closed.</summary>
public abstract record AdmissionDecision
{
    private protected AdmissionDecision()
    {
    }
}

/// <summary>The job may run now; <see cref="Lease"/> holds its slot and its project until disposed (plan 07 A1).</summary>
/// <param name="Lease">Disposed by the listener when the run and its report are done — or abandoned; a second dispose is harmless.</param>
public sealed record Admitted(IDisposable Lease) : AdmissionDecision;

/// <summary>The job stays where it is; the dispatcher logs the first deferral of an episode and the admission that ends it.</summary>
/// <param name="Reason">Which rule deferred it.</param>
public sealed record Deferred(DeferralReason Reason) : AdmissionDecision;
