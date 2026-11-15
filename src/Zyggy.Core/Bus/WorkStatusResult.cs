using Zyggy.Core.Configuration;
using Zyggy.Core.Envelope;

namespace Zyggy.Core.Bus;

/// <summary>
/// What a session's hook knows about the session when it publishes a status (deliverable 18, "Live work is visible"). The five optional
/// values are written only with <see cref="SupervisionDetail.Full"/>.
/// </summary>
/// <param name="Project">The project's registry name.</param>
/// <param name="Session">The session's 8-character id.</param>
/// <param name="Since">When the session started.</param>
/// <param name="Activity">When the session's last turn ended.</param>
/// <param name="Branch">The branch checked out, when known.</param>
/// <param name="Head">The short commit sha, when known.</param>
/// <param name="Dirty">The count of uncommitted files, when known.</param>
/// <param name="Spec">The spec the branch belongs to, when exactly one matches.</param>
/// <param name="Step">The step the session stated, when it did.</param>
public sealed record WorkStatusRequest(
    string Project,
    string Session,
    DateTimeOffset Since,
    DateTimeOffset Activity,
    string? Branch = null,
    string? Head = null,
    int? Dirty = null,
    string? Spec = null,
    string? Step = null);

/// <summary>How a status is published on this machine: the <c>supervision</c> settings of <c>node.json</c> plus whether the caller is an unattended run.</summary>
/// <param name="Detail">How much a status carries.</param>
/// <param name="Enabled"><c>supervision.enabled</c>; <see langword="false"/> writes nothing.</param>
/// <param name="HooksOff"><see langword="true"/> inside a job run (<c>ZYGGY_HOOKS=off</c>): a job's state is already on the bus, nothing is written.</param>
public sealed record WorkStatusOptions(SupervisionDetail Detail, bool Enabled = true, bool HooksOff = false);

/// <summary>What <see cref="WorkStatusPublisher"/> did (deliverable 18).</summary>
public abstract record WorkStatusResult
{
    private protected WorkStatusResult()
    {
    }
}

/// <summary>The status is in this machine's own <c>status/</c> folder, committed.</summary>
/// <param name="Id">The status envelope's id.</param>
/// <param name="Path">Its path in the checkout.</param>
/// <param name="Pushed"><see langword="false"/> when the commit stays local until the next write.</param>
public sealed record WorkStatusPublished(EnvelopeId Id, string Path, bool Pushed) : WorkStatusResult;

/// <summary>The session's status file was removed, or there was none.</summary>
/// <param name="Pushed"><see langword="false"/> when the commit stays local until the next write.</param>
public sealed record WorkStatusRemoved(bool Pushed) : WorkStatusResult;

/// <summary>Nothing was written, on purpose.</summary>
/// <param name="Why"><c>hooks off</c> or <c>supervision disabled</c>.</param>
public sealed record WorkStatusSkipped(string Why) : WorkStatusResult;

/// <summary>A value breaks its rule or matches a secret pattern; nothing was signed or written, and the value is kept nowhere.</summary>
/// <param name="Field">The field at fault.</param>
/// <param name="Detail">Why; for a secret pattern, <c>matches secret pattern &lt;name&gt;</c>.</param>
public sealed record WorkStatusRefused(string Field, string Detail) : WorkStatusResult;

/// <summary>Signing or the bus write failed; nothing is on the bus.</summary>
/// <param name="Stage">The stage that failed (<c>sign</c>, <c>secret-store</c>, <c>pull</c>, <c>add</c>, <c>rm</c>, <c>commit</c>, <c>lock</c>).</param>
/// <param name="Detail">The failure text.</param>
public sealed record WorkStatusFailed(string Stage, string Detail) : WorkStatusResult;
