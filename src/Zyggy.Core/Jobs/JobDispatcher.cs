using Microsoft.Extensions.Logging;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Policy;

namespace Zyggy.Core.Jobs;

/// <summary>
/// The §6 admission rules, in order, for a job the listener is about to claim (spec 07): outside the working window only
/// <c>priority: high</c> runs; at most <c>maxConcurrentJobs</c> leases at once; one run per project — not in flight here and
/// <see cref="ProjectLock.IsHeld"/> false. An <see cref="Admitted"/> answer takes the slot and the project atomically (plan 07 A1); its
/// lease gives them back. A deferral is logged once per episode, the admission that ends it once (§11). Nothing here touches the bus.
/// </summary>
public sealed partial class JobDispatcher
{
    private readonly IPolicySource _policy;

    private readonly TimeProvider _clock;

    private readonly ILogger<JobDispatcher> _log;

    private readonly Lock _gate = new();

    private readonly HashSet<Lease> _leases = [];

    private readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);

    private readonly HashSet<string> _deferred = new(StringComparer.Ordinal);

    private bool _warnedConcurrency;

    /// <summary>Initializes a new instance of the <see cref="JobDispatcher"/> class.</summary>
    /// <param name="policy">The node's policy (<c>maxConcurrentJobs</c>, <c>workHours</c>).</param>
    /// <param name="clock">The clock the work-hours rule reads.</param>
    /// <param name="log">The logger.</param>
    public JobDispatcher(IPolicySource policy, TimeProvider clock, ILogger<JobDispatcher> log)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(log);
        _policy = policy;
        _clock = clock;
        _log = log;
    }

    /// <summary>Gets how many admitted jobs hold a lease right now (the <c>jobsRunning</c> health field).</summary>
    public int JobsRunning
    {
        get
        {
            lock (_gate)
            {
                return _leases.Count;
            }
        }
    }

    /// <summary>Applies the rules to <paramref name="job"/> at the clock's current instant.</summary>
    /// <param name="job">A job envelope.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see cref="Admitted"/> with the lease, or <see cref="Deferred"/> with the first rule that failed.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="job"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="job"/> is not a job envelope.</exception>
    public async Task<AdmissionDecision> AdmitAsync(Envelope.Envelope job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Type != EnvelopeType.Job || job.Job is null)
        {
            throw new ArgumentException("The envelope is not a job.", nameof(job));
        }

        NodePolicy policy = await _policy.GetAsync(ct).ConfigureAwait(false);
        string id = job.Header.Id.Value;
        using IDisposable? scope = Scope(job);
        lock (_gate)
        {
            if (!_warnedConcurrency && policy.MaxConcurrentJobs > 4)
            {
                _warnedConcurrency = true;
                LogConcurrencyAdvisory(policy.MaxConcurrentJobs);
            }

            if (job.Header.Priority != EnvelopePriority.High && policy.WorkHours is { } hours && !hours.Contains(_clock.GetUtcNow()))
            {
                return Defer(id, DeferralReason.OutsideWorkHours);
            }

            if (_leases.Count >= policy.MaxConcurrentJobs)
            {
                return Defer(id, DeferralReason.NoSlot);
            }

            string project = job.Job.Project;
            string? projectKey = Path.IsPathFullyQualified(project) ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(project)) : null;
            if (projectKey is not null && (_inFlight.Contains(projectKey) || ProjectLock.IsHeld(projectKey)))
            {
                return Defer(id, DeferralReason.ProjectBusy);
            }

            var lease = new Lease(this, projectKey);
            _leases.Add(lease);
            if (projectKey is not null)
            {
                _inFlight.Add(projectKey);
            }

            if (_deferred.Remove(id))
            {
                LogAdmittedAfterDeferral(id);
            }

            return new Admitted(lease);
        }
    }

    /// <summary>This dispatcher's rules as the <see cref="JobGate"/> the listener consumes.</summary>
    /// <returns>The gate.</returns>
    public JobGate AsGate() => AdmitAsync;

    private Deferred Defer(string id, DeferralReason reason)
    {
        if (_deferred.Add(id))
        {
            string reasonText = reason.ToString();
            LogDeferred(id, reasonText);
        }

        return new Deferred(reason);
    }

    private void Release(Lease lease)
    {
        lock (_gate)
        {
            if (_leases.Remove(lease) && lease.ProjectKey is not null)
            {
                _inFlight.Remove(lease.ProjectKey);
            }
        }
    }

    private IDisposable? Scope(Envelope.Envelope job)
    {
        if (!_log.IsEnabled(LogLevel.Information))
        {
            return null;
        }

        var scope = new Dictionary<string, object>
        {
            ["tenant"] = job.Header.Tenant.Value,
            ["machine"] = job.Header.To.Value,
            ["phase"] = "claim",
            ["jobId"] = job.Header.Id.Value,
        };
        return _log.BeginScope(scope);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "job {Id} deferred: {Reason}")]
    private partial void LogDeferred(string id, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "job {Id} admitted after deferral")]
    private partial void LogAdmittedAfterDeferral(string id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "maxConcurrentJobs {Max} > 4: more than four parallel claude runs is unlikely to be intended")]
    private partial void LogConcurrencyAdvisory(int max);

    /// <summary>The slot and project of one admitted job; disposing gives both back (idempotent).</summary>
    private sealed class Lease(JobDispatcher owner, string? projectKey) : IDisposable
    {
        private int _disposed;

        public string? ProjectKey { get; } = projectKey;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Release(this);
            }
        }
    }
}
