using System.Text.RegularExpressions;

namespace Zyggy.Core.Outlook;

/// <summary>
/// The guard's answer (spec 58 "Guard"). There is no "allow" and no "ask": <see cref="Passed"/> leaves the call to the owner's
/// permission prompt, a refusal stops it before the prompt with its reason.
/// </summary>
/// <param name="Reason">The refusal reason, or <see langword="null"/> when the call passes to the prompt.</param>
internal sealed record OutlookGuardDecision(string? Reason)
{
    /// <summary>Gets the decision that defers to the permission prompt.</summary>
    public static OutlookGuardDecision Pass { get; } = new((string?)null);

    /// <summary>Gets a value indicating whether the call passes to the permission prompt.</summary>
    public bool Passed => Reason is null;
}

/// <summary>
/// The guard of the outlook actions (spec 58 slice 6, AC-10, AC-11): refuses every send and move outside an attended session, outside
/// the policy record and <c>node.json</c>, or outside the send and move rules, before the permission prompt, with one log row per
/// refusal. It never allows and never asks; a read it needs that fails makes it refuse (fail closed). A refusal never echoes a value
/// of the request. A call that passes writes no row here: the row after the prompt and the action is the action tool's
/// (<see cref="OutlookActions"/>, slice 7), which runs it again just before it acts.
/// </summary>
/// <remarks>
/// The order is the spec's action decision: attended run, then <c>outlook.enabled</c> and the policy record's scope, then
/// <c>outlook.actions</c>, then the rules of the action. The send rules need no Outlook read; a move reads the default store's id,
/// the destination folder and the item, and never writes.
/// </remarks>
/// <param name="access">The Outlook access (reads only: the default store, a folder, an item).</param>
/// <param name="policy">The evaluated policy record and <c>outlook.enabled</c>.</param>
/// <param name="settings">The action keys of <c>node.json</c>.</param>
/// <param name="run">Whether the run is attended.</param>
/// <param name="log">The action log.</param>
/// <param name="clock">The clock of the log rows.</param>
internal sealed partial class OutlookGuard(
    IOutlookAccess access,
    OutlookPolicyDecision policy,
    OutlookActionSettings settings,
    OutlookRunContext run,
    IOutlookActionLog log,
    TimeProvider clock)
{
    // Deleted Items, Archive and Inbox are always allowed destinations (spec 58 "Move (file)"), as in the m365 guard.
    private static readonly string[] AlwaysAllowed = ["Deleted Items", "Archive", "Inbox"];

    // Outlook's recoverable-items folders: a move there is a permanent delete.
    private static readonly string[] Recoverable = ["Recoverable Items", "Deletions", "Purges", "Versions", "DiscoveryHolds"];

    // Folders a filing never targets: the send path and the drafts (with their subfolders).
    private static readonly string[] NotFiling = ["Outbox", "Drafts"];

    /// <summary>Checks one send.</summary>
    /// <param name="request">The send.</param>
    /// <returns>Pass to the prompt, or the refusal.</returns>
    public OutlookGuardDecision CheckSend(OutlookSendRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reason = Common(OutlookAction.Send) ?? SendRule(request);
        return reason is null
            ? OutlookGuardDecision.Pass
            : Refuse(OutlookAction.Send, reason, request.InReplyTo, [.. request.To, .. request.Cc], request.Subject, destination: null);
    }

    /// <summary>Checks one move.</summary>
    /// <param name="request">The move.</param>
    /// <returns>Pass to the prompt, or the refusal.</returns>
    public OutlookGuardDecision CheckMove(OutlookMoveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string? reason;
        try
        {
            reason = Common(OutlookAction.Move) ?? MoveRule(request);
        }
        catch (OutlookUnavailableException)
        {
            reason = OutlookReader.Describe(OutlookRefusal.OutlookNotAvailable);
        }

        return reason is null
            ? OutlookGuardDecision.Pass
            : Refuse(OutlookAction.Move, reason, request.Reference, [], subject: null, request.Destination);
    }

    /// <summary>
    /// Checks only what every call of <paramref name="action"/> shares — attended run, <c>outlook.enabled</c> and the record's scope,
    /// <c>outlook.actions</c> — before the request itself is known (a send by draft number, spec 58 slice 7), with one row on refusal.
    /// </summary>
    /// <param name="action">The action.</param>
    /// <returns>Pass, or the refusal.</returns>
    public OutlookGuardDecision CheckRun(OutlookAction action) =>
        Common(action) is { } reason ? Refuse(action, reason, reference: null, [], subject: null, destination: null) : OutlookGuardDecision.Pass;

    /// <summary>Whether a text holds a character that can fake or hide a line where it is shown: a control or a bidi control.</summary>
    /// <param name="text">The text.</param>
    /// <returns>Whether it does.</returns>
    public static bool HasControl(string text) => text.Any(IsHidingCharacter);

    private static bool IsHidingCharacter(char c) => char.IsControl(c) || c is (>= '‪' and <= '‮') or (>= '⁦' and <= '⁩') or '‎' or '‏' or '؜';

    private static string Word(OutlookAction action) => action == OutlookAction.Send ? "send" : "move";

    private static bool IsAddress(string address) => Address().IsMatch(address);

    private static string Normalised(string folder) => string.Join('/', folder.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));

    private static bool Under(string folder, string root) =>
        string.Equals(folder, root, StringComparison.OrdinalIgnoreCase) || folder.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);

    // The rules every action shares, none of which touches Outlook.
    private string? Common(OutlookAction action)
    {
        if (!run.Attended)
        {
            return "not attended";
        }

        if (policy.RefusalFor(Word(action)) is { } refusal)
        {
            return OutlookReader.Describe(refusal);
        }

        return settings.Actions.Contains(Word(action)) ? null : "action not enabled";
    }

    private string? SendRule(OutlookSendRequest request)
    {
        if (request.Attachments.Count > 0)
        {
            return "attachments are not allowed";
        }

        if (request.Bcc.Count > 0)
        {
            return "Bcc is not allowed";
        }

        if (request.BodyFormat != OutlookBodyFormat.PlainText)
        {
            return "only plain-text bodies";
        }

        if (request.SendingAccount is not null || request.Sender is not null || request.ReplyTo.Count > 0)
        {
            return "sender, account and reply-to cannot be changed";
        }

        var recipients = request.To.Concat(request.Cc).ToList();

        // Slice 7: the prompt shows the message as text; a line break in a header, or a terminal or bidi control anywhere, could fake
        // a line of it (another "To:"), or hide a part of the body.
        if (recipients.Append(request.Subject).Any(HasControl) || request.Body.Any(c => c is not ('\r' or '\n' or '\t') && IsHidingCharacter(c)))
        {
            return "control characters in the message";
        }

        if (recipients.Count == 0)
        {
            return "no recipient";
        }

        if (recipients.Count > settings.SendMaxRecipients)
        {
            return $"more than {settings.SendMaxRecipients} recipients";
        }

        if (!recipients.All(IsAddress))
        {
            return "malformed address";
        }

        // The whole body counts, the quoted history of a reply included (AC-10; Q20 is open on whether that stays so).
        return request.Body.EnumerateRunes().Count() > settings.SendBodyMaxChars ? $"body over {settings.SendBodyMaxChars} characters" : null;
    }

    private string? MoveRule(OutlookMoveRequest request)
    {
        var destination = Normalised(request.Destination);
        if (request.Permanent || destination.Split('/').Any(part => Recoverable.Contains(part, StringComparer.OrdinalIgnoreCase)))
        {
            return "permanent delete not allowed";
        }

        if (destination.Length == 0 || HasControl(destination))
        {
            return "folder not found";
        }

        // Slice 7: filing never puts a mail in the send path or among the drafts (no draft is saved in Outlook, AC-7).
        if (NotFiling.Contains(destination.Split('/')[0], StringComparer.OrdinalIgnoreCase))
        {
            return "not a filing folder";
        }

        var wellKnown = AlwaysAllowed.Contains(destination, StringComparer.OrdinalIgnoreCase);
        if (!wellKnown && settings.MoveExcludedFolders.Any(excluded => Under(destination, Normalised(excluded))))
        {
            return "folder excluded";
        }

        var store = access.DefaultStoreId();
        if (request.Store is { } other && !string.Equals(other, store, StringComparison.Ordinal))
        {
            return "only the default store";
        }

        if (access.Count(store, destination) is null)
        {
            return "folder not found";
        }

        return access.Find(store, request.Reference) is null ? "item not found" : null;
    }

    private OutlookGuardDecision Refuse(OutlookAction action, string reason, string? reference, IReadOnlyList<string> recipients, string? subject, string? destination)
    {
        log.Append(new OutlookActionLogRow(clock.GetUtcNow(), action, OutlookActionOutcome.Refused, reason, reference, recipients, subject, destination));
        return new OutlookGuardDecision(reason);
    }

    [GeneratedRegex(@"\A[^@\s<>()\[\],;:""]+@[^@\s<>()\[\],;:""]+\.[^@\s<>()\[\],;:""]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex Address();
}
