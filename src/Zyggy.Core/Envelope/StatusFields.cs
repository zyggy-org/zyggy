namespace Zyggy.Core.Envelope;

/// <summary>
/// The fields of a <c>status</c> envelope: the state of one live Claude Code session, sent to Central by the machine it runs on
/// (deliverable 18, "Live work is visible"). The first four are always written; the others only with <c>supervision.detail: full</c>.
/// A status never carries prompt text, code or a file name, and its body is empty.
/// </summary>
/// <param name="Project">The <c>project</c>: the registry name, 1–100 characters of <c>[a-z0-9-]</c> (mandatory).</param>
/// <param name="Session">The <c>session</c>: 8 characters of <c>[a-z0-9]</c> (mandatory).</param>
/// <param name="Since">The <c>since</c> timestamp: when the session started (mandatory).</param>
/// <param name="Activity">The <c>activity</c> timestamp: when the session's last turn ended (mandatory).</param>
/// <param name="Branch">The <c>branch</c> checked out: 1–200 characters without control characters.</param>
/// <param name="Head">The <c>head</c> commit: 7–40 lowercase hexadecimal characters.</param>
/// <param name="Dirty">The <c>dirty</c> count of uncommitted files, 0 or more.</param>
/// <param name="Spec">The <c>spec</c> the branch belongs to: a repository-relative path with <c>/</c> separators.</param>
/// <param name="Step">The <c>step</c> the session stated: 1–60 characters of <c>[a-z0-9 -]</c>.</param>
public sealed record StatusFields(
    string Project,
    string Session,
    DateTimeOffset Since,
    DateTimeOffset Activity,
    string? Branch = null,
    string? Head = null,
    int? Dirty = null,
    string? Spec = null,
    string? Step = null);
