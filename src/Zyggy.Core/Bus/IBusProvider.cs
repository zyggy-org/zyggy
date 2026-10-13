namespace Zyggy.Core.Bus;

/// <summary>
/// The §9 seam that hides the git host (GitHub in v1): the doorbell — one conditional head check per tick (founding spec §5
/// poll loop items 1–2) — plus pull and push of the local checkout. The host's REST API, URL shapes, headers and credential
/// handling live only behind this interface; <see cref="BusPaths"/>, the repository logic and the poller state machine are
/// host-agnostic. An implementation never throws for an expected failure: every outcome is a result.
/// </summary>
public interface IBusProvider
{
    /// <summary>Gets the description of the remote (owner, repository, branch, URLs — both filled in by the provider); never a credential.</summary>
    BusRemote Remote { get; }

    /// <summary>Gets the base address the doorbell is rung at (the host's API), for the reachability report; never a credential.</summary>
    Uri ApiBaseUri { get; }

    /// <summary>Rings the doorbell: asks the host whether the branch moved since <paramref name="etag"/> (§5 items 1–2).</summary>
    /// <param name="etag">The validator from the last <see cref="Changed"/> answer, or <see langword="null"/> before the first.</param>
    /// <param name="ct">Cancellation token; the caller's own cancellation propagates.</param>
    /// <returns>What the host said, as a closed outcome; never throws for a network, auth or server failure.</returns>
    Task<HeadProbe> ProbeHeadAsync(string? etag, CancellationToken ct);

    /// <summary>Brings the remote branch into the local checkout (<c>pull --rebase origin &lt;branch&gt;</c>, §5 "Git operations").</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Whether the pull succeeded, with a short redacted detail.</returns>
    Task<BusSyncResult> PullAsync(CancellationToken ct);

    /// <summary>Pushes the local branch once (<c>push origin HEAD:&lt;branch&gt;</c>); the retry rule belongs to the repository layer (§5).</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see cref="PushSucceeded"/>, <see cref="Rejected"/> (non-fast-forward: rebase and retry) or <see cref="PushFailed"/>.</returns>
    Task<PushResult> PushAsync(CancellationToken ct);
}
