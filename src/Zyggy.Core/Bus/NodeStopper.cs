using System.Globalization;

using Zyggy.Core.Health;
using Zyggy.Core.Processes;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Bus;

/// <summary>What <c>zyggy status</c> knows about the listener of this checkout (spec 61 §4).</summary>
/// <param name="Owner">The owner record; <see langword="null"/> when there is none.</param>
/// <param name="Alive">Whether the recorded process still runs (same pid, same start).</param>
/// <param name="Refused">The refused-start record; <see langword="null"/> when there is none.</param>
/// <param name="Stray">A start was refused after the holder started and the holder still runs.</param>
/// <param name="VersionDiffers">The live holder runs another version than the one installed.</param>
internal sealed record ListenerFindings(ListenerRecord? Owner, bool Alive, RefusedStart? Refused, bool Stray, bool VersionDiffers);

/// <summary>The outcome of <c>zyggy node stop</c>.</summary>
internal enum NodeStopResult
{
    /// <summary>The listener was ended and the lock is free (exit 0).</summary>
    Stopped,

    /// <summary>No listener holds the checkout (exit 0).</summary>
    NotRunning,

    /// <summary>The recorded pid, or the node answering on the health port, is not this user's listener of this checkout: nothing touched (exit 5).</summary>
    Foreign,

    /// <summary>The listener could not be ended, or could not be identified (exit 6).</summary>
    Failed,
}

/// <summary>The outcome and the one line the verb prints.</summary>
/// <param name="Result">The outcome.</param>
/// <param name="Line">The owner-facing line.</param>
internal sealed record NodeStopOutcome(NodeStopResult Result, string Line);

/// <summary>Reads the listener records of a checkout for <c>zyggy status</c> (spec 61 §4).</summary>
internal static class ListenerInspection
{
    /// <summary>The findings for <paramref name="checkoutRoot"/>, checked against the live processes and the installed version.</summary>
    public static ListenerFindings Inspect(string checkoutRoot, IProcessTable processes, string installedVersion)
    {
        ListenerRecord? owner = ListenerOwner.ReadOwner(checkoutRoot);
        RefusedStart? refused = ListenerOwner.ReadRefused(checkoutRoot);
        if (owner is null)
        {
            return new ListenerFindings(null, false, refused, false, false);
        }

        bool alive = ListenerOwner.IsSameProcess(owner, processes.Find(owner.Pid));
        bool stray = alive && refused is not null && refused.RefusedUtc > owner.StartedUtc;
        bool versionDiffers = alive && !string.Equals(owner.Version, installedVersion, StringComparison.Ordinal);
        return new ListenerFindings(owner, alive, refused, stray, versionDiffers);
    }
}

/// <summary>
/// <c>zyggy node stop</c> (spec 61 §4): ends the listener that holds this checkout's listen lock — only when the owner record names a
/// running <c>zyggy-node</c> of this user with the recorded start time, and the node answering on the health port (if any) is that same
/// process for this tenant and machine — then waits until the lock is free.
/// </summary>
internal sealed class NodeStopper
{
    /// <summary>The process name of the node binary.</summary>
    internal const string NodeProcessName = "zyggy-node";

    private readonly string _checkoutRoot;
    private readonly IProcessTable _processes;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _lockWait;
    private readonly bool _windows;

    public NodeStopper(string checkoutRoot, IProcessTable processes, TimeProvider clock, TimeSpan? lockWait = null, bool? windows = null)
    {
        _checkoutRoot = checkoutRoot;
        _processes = processes;
        _clock = clock;
        _lockWait = lockWait ?? TimeSpan.FromSeconds(15);
        _windows = windows ?? OperatingSystem.IsWindows();
    }

    /// <summary>Stops the listener, given what the health URL answered.</summary>
    /// <param name="health">What the health URL answered (read by the caller, once, before this call).</param>
    /// <param name="tenant">This node's tenant from <c>node.json</c>.</param>
    /// <param name="machine">This node's machine from <c>node.json</c>.</param>
    /// <param name="healthUrl">The health URL, for the owner-facing line.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The outcome and its line.</returns>
    public async Task<NodeStopOutcome> StopAsync(HealthProbe health, TenantId tenant, MachineName machine, Uri healthUrl, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(healthUrl);

        // Nothing holds the checkout: whatever a record says, there is no listener to stop, and nothing is touched.
        if (await LockFreeAsync(TimeSpan.Zero, ct).ConfigureAwait(false))
        {
            return new NodeStopOutcome(NodeStopResult.NotRunning, "no listener is running");
        }

        ListenerRecord? owner = ListenerOwner.ReadOwner(_checkoutRoot);
        ProcessFacts? facts = owner is null ? null : _processes.Find(owner.Pid);
        if (owner is null || facts is null)
        {
            return new NodeStopOutcome(
                NodeStopResult.Failed,
                "the bus checkout is held by a listener that left no owner record (a zyggy-node before 0.30.1); end it with: "
                    + (_windows ? "Stop-Process -Name " + NodeProcessName + " -Force" : "pkill -u \"$(id -u)\" -x " + NodeProcessName));
        }

        string pid = Number(owner.Pid);
        if (!string.Equals(facts.Name, NodeProcessName, StringComparison.OrdinalIgnoreCase) || !facts.OwnedByCurrentUser || !ListenerOwner.IsSameProcess(owner, facts))
        {
            return new NodeStopOutcome(
                NodeStopResult.Foreign,
                "pid " + pid + " is not this user's " + NodeProcessName + " started at " + ListenerOwner.Timestamp(owner.ProcessStartUtc)
                    + " (it is " + facts.Name + (facts.OwnedByCurrentUser ? string.Empty : " of another user")
                    + (facts.StartedUtc is { } started ? ", started " + ListenerOwner.Timestamp(started) : string.Empty) + "); nothing was touched");
        }

        // A node of this checkout always holds its health port while it runs; another answer there is not this listener.
        int interrupted = 0;
        string? foreign = health switch
        {
            Reachable { Health: var h } when h.Pid == owner.Pid && h.Tenant == tenant.Value && h.Machine == machine.Value => null,
            Reachable { Health: var h } => "it answers as " + (h.Pid is { } other ? "pid " + Number(other) : "a node without a pid") + " of " + h.Tenant + "/" + h.Machine,
            Health.Unreachable { Detail: "connection refused" or "timeout" } => null,
            Health.Unreachable { Detail: var detail } => "it answers " + detail,
            _ => null,
        };
        if (foreign is not null)
        {
            return new NodeStopOutcome(
                NodeStopResult.Foreign,
                "the listener on " + healthUrl + " is not " + NodeProcessName + " pid " + pid + " of " + tenant.Value + "/" + machine.Value + " (" + foreign + "); nothing was touched");
        }

        if (health is Reachable { Health: var mine })
        {
            interrupted = mine.JobsRunning;
        }

        string remedy = _windows ? "Stop-Process -Id " + pid + " -Force" : "kill " + pid;
        if (_processes.EndTree(owner.Pid, TimeSpan.FromSeconds(10)) is { } why)
        {
            return new NodeStopOutcome(NodeStopResult.Failed, "could not end " + NodeProcessName + " pid " + pid + " (" + why + "); end it with: " + remedy);
        }

        if (!await LockFreeAsync(_lockWait, ct).ConfigureAwait(false))
        {
            return new NodeStopOutcome(NodeStopResult.Failed, NodeProcessName + " pid " + pid + " was ended but the bus checkout is still held; end it with: " + remedy);
        }

        ListenerOwner.Clear(_checkoutRoot);
        return new NodeStopOutcome(
            NodeStopResult.Stopped,
            "stopped " + NodeProcessName + " pid " + pid + " (" + Number(interrupted) + " run(s) interrupted; their claims resume at the next start)");
    }

    private async Task<bool> LockFreeAsync(TimeSpan wait, CancellationToken ct)
    {
        IDisposable? probe = await BusCheckoutLock.AcquireAsync(_checkoutRoot, BusCheckoutLock.ListenLockName, wait, _clock, ct).ConfigureAwait(false);
        probe?.Dispose();
        return probe is not null;
    }

    internal static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
