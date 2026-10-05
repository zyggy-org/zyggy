using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Zyggy.Core.M365.Graph;

namespace Zyggy.Core.M365.Guard;

/// <summary>The three action tools of the pinned m365 server.</summary>
internal enum GuardAction
{
    Send,
    Upload,
    Move,
}

/// <summary>
/// The PreToolUse guard's decision table — <c>m365-guard.sh</c> (spec 23 D7, spec 33 AC-20): every send, upload or move outside the
/// instance policy is refused before Claude Code shows its permission prompt; a call within policy passes to that prompt. It never
/// allows and never asks. At most two Graph reads, reads only; a failed read is <see cref="GuardDecision.Fail"/> (fail closed).
/// </summary>
internal sealed partial class GuardPolicy(M365Configuration configuration, IGraphReader reader)
{
    private static readonly string[] MoveNames = ["deleteditems", "archive", "inbox"];

    /// <summary>The action of a tool, or <see langword="null"/> for any other tool (nothing to guard).</summary>
    public static GuardAction? ActionOf(string toolName) => toolName switch
    {
        "mcp__m365__send-shared-mailbox-mail" => GuardAction.Send,
        "mcp__m365__upload-file-content" => GuardAction.Upload,
        "mcp__m365__move-shared-mailbox-message" => GuardAction.Move,
        _ => null,
    };

    public async Task<GuardDecision> EvaluateAsync(GuardAction action, JsonElement toolInput, CancellationToken cancellationToken)
    {
        var name = action.ToString().ToLowerInvariant();
        if (!configuration.ActionsEnabled.Contains(name))
        {
            return new GuardDecision.Deny("action disabled for this instance");
        }

        var args = JsonView.Of(toolInput)!;
        return action switch
        {
            GuardAction.Send => Send(args),
            GuardAction.Upload => await UploadAsync(args, cancellationToken).ConfigureAwait(false),
            _ => await MoveAsync(args, cancellationToken).ConfigureAwait(false),
        };
    }

    // --- send-shared-mailbox-mail ---------------------------------------------------------------------------------------

    private GuardDecision Send(JsonView args)
    {
        if (ExpectArgs(args, "userId", "body") is { } unexpected)
        {
            return unexpected;
        }

        if (RequireMailbox(args) is { } other)
        {
            return other;
        }

        if (JsonView.Of(args.Get("body")) is not { } body)
        {
            return Deny("malformed body (an object with Message is expected)");
        }

        if (!body.CiUnique)
        {
            return Deny("the body names a field twice");
        }

        if (body.Keys.Select(JsonView.AsciiLower).Any(k => k is not ("message" or "savetosentitems")))
        {
            return Deny("unexpected body field");
        }

        if (body.CiGet("savetosentitems") is { ValueKind: not (JsonValueKind.Null or JsonValueKind.True) })
        {
            return Deny("saveToSentItems must stay true");
        }

        if (JsonView.Of(body.CiGet("message")) is not { } message)
        {
            return Deny("malformed body (an object with Message is expected)");
        }

        if (!message.CiUnique)
        {
            return Deny("the message names a field twice");
        }

        // The fields a prompt shows and a reader can check; anything else could hide a recipient, a header or content.
        foreach (var key in message.Keys.Select(JsonView.AsciiLower))
        {
            switch (key)
            {
                case "subject" or "body" or "torecipients" or "ccrecipients" or "importance":
                    break;
                case "attachments" when !IsEmptyList(message.CiGet("attachments")):
                    return Deny("attachments are not allowed");
                case "bccrecipients" when !IsEmptyList(message.CiGet("bccrecipients")):
                    return Deny("Bcc is not allowed");
                case "attachments" or "bccrecipients":
                    break;
                case "from" or "sender" or "replyto":
                    return Deny("from, sender and replyTo are not allowed");
                default:
                    return Deny("message field not allowed");
            }
        }

        if (JsonView.Of(message.CiGet("body")) is not { } messageBody)
        {
            return Deny("only plain-text bodies (body.contentType text)");
        }

        if (!messageBody.CiUnique)
        {
            return Deny("the message body names a field twice");
        }

        if (messageBody.Keys.Select(JsonView.AsciiLower).Any(k => k is not ("content" or "contenttype")))
        {
            return Deny("message body field not allowed");
        }

        if (messageBody.CiGet("contenttype") is not { ValueKind: JsonValueKind.String } type || JsonView.AsciiLower(type.GetString()!) != "text")
        {
            return Deny("only plain-text bodies");
        }

        var content = messageBody.CiGet("content");
        if (content is { ValueKind: not (JsonValueKind.String or JsonValueKind.Null) })
        {
            return Deny("malformed body content");
        }

        var characters = content is { ValueKind: JsonValueKind.String } text ? text.GetString()!.EnumerateRunes().Count() : 0;
        if (characters > configuration.SendBodyMaxChars)
        {
            return Deny($"body over {configuration.SendBodyMaxChars} characters");
        }

        var to = message.CiGet("torecipients");
        var cc = message.CiGet("ccrecipients");
        if (!IsListOrAbsent(to) || !IsListOrAbsent(cc))
        {
            return Deny("malformed recipients");
        }

        var recipients = Items(to).Concat(Items(cc)).ToList();
        var addresses = new List<string>();
        foreach (var recipient in recipients)
        {
            if (JsonView.Of(recipient) is not { } r
                || JsonView.Of(r.Get("emailAddress")) is not { } email
                || email.Get("address") is not { ValueKind: JsonValueKind.String } address)
            {
                return Deny("malformed address");
            }

            addresses.Add(address.GetString()!);
        }

        if (addresses.Count == 0)
        {
            return Deny("no recipient");
        }

        if (addresses.Count > configuration.SendMaxRecipients)
        {
            return Deny($"more than {configuration.SendMaxRecipients} recipients");
        }

        return addresses.All(a => M365Grammar.Upn().IsMatch(a)) ? GuardDecision.Pass : Deny("malformed address");
    }

    // --- upload-file-content --------------------------------------------------------------------------------------------

    private async Task<GuardDecision> UploadAsync(JsonView args, CancellationToken cancellationToken)
    {
        if (ExpectArgs(args, "driveId", "driveItemId", "body") is { } unexpected)
        {
            return unexpected;
        }

        var drive = args.StringOrEmpty("driveId");
        if (configuration.WriteDriveId.Length == 0 || drive != configuration.WriteDriveId)
        {
            return Deny("drive not allowed");
        }

        var match = NewFileForm().Match(args.StringOrEmpty("driveItemId"));
        if (!match.Success)
        {
            return Deny("only the new-file form <parent-id>:/<name>:");
        }

        var parent = match.Groups[1].Value;
        var name = match.Groups[2].Value;
        if (!M365Grammar.ItemId().IsMatch(parent))
        {
            return Deny("malformed parent id");
        }

        if (name.Contains('/', StringComparison.Ordinal) || name.Contains('\\', StringComparison.Ordinal) || name.Contains("..", StringComparison.Ordinal)
            || name.Any(c => c < ' ' || c == '\u007f') || Encoding.UTF8.GetByteCount(name) > 255 || name.StartsWith('.'))
        {
            return Deny("invalid file name");
        }

        var dot = name.LastIndexOf('.');
        if (dot < 0 || !configuration.UploadExtensions.Contains(JsonView.AsciiLower(name[(dot + 1)..])))
        {
            return Deny("extension not allowed");
        }

        // The content: a base64 string the server decodes; its decoded size is what Graph stores.
        var content = (args.Get("body") is { ValueKind: JsonValueKind.String } b ? b.GetString()! : string.Empty).Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal);
        if (!Base64Shape().IsMatch(content) || content.Length % 4 != 0)
        {
            return Deny("content is not base64");
        }

        int size;
        try
        {
            size = Convert.FromBase64String(content).Length;
        }
        catch (FormatException)
        {
            return Deny("content is not base64");
        }

        if (size > configuration.UploadMaxBytes)
        {
            return Deny($"content over {configuration.UploadMaxBytes} bytes");
        }

        var kind = await reader.ItemKindAsync(drive, parent, cancellationToken).ConfigureAwait(false);
        if (kind.Failure is not null)
        {
            return new GuardDecision.Fail("graph read item-kind failed");
        }

        if (kind.Value != ItemKind.Folder)
        {
            return Deny("parent is not a folder");
        }

        var exists = await reader.ItemExistsAsync(drive, parent, name, cancellationToken).ConfigureAwait(false);
        if (exists.Failure is not null)
        {
            return new GuardDecision.Fail("graph read item-exists failed");
        }

        return exists.Value == ItemPresence.Absent ? GuardDecision.Pass : Deny("target exists (would overwrite)");
    }

    // --- move-shared-mailbox-message ------------------------------------------------------------------------------------

    private async Task<GuardDecision> MoveAsync(JsonView args, CancellationToken cancellationToken)
    {
        if (ExpectArgs(args, "userId", "messageId", "body") is { } unexpected)
        {
            return unexpected;
        }

        if (RequireMailbox(args) is { } other)
        {
            return other;
        }

        if (!M365Grammar.Id().IsMatch(args.StringOrEmpty("messageId")))
        {
            return Deny("malformed message id");
        }

        if (JsonView.Of(args.Get("body")) is not { } body)
        {
            return Deny("malformed body (an object with DestinationId is expected)");
        }

        if (!body.CiUnique)
        {
            return Deny("the body names a field twice");
        }

        if (body.Keys.Any(k => JsonView.AsciiLower(k) != "destinationid"))
        {
            return Deny("unexpected body field");
        }

        var destination = body.Values.Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).FirstOrDefault() ?? string.Empty;
        if (destination.Length == 0)
        {
            return Deny("destination not allowed");
        }

        if (MoveNames.Contains(JsonView.AsciiLower(destination)))
        {
            return GuardDecision.Pass;
        }

        // A folder id of the mailbox that is not an excluded folder; every other well-known name or id is refused.
        if (!M365Grammar.Id().IsMatch(destination))
        {
            return Deny("destination not allowed");
        }

        var folders = await reader.MailFoldersAsync(cancellationToken).ConfigureAwait(false);
        if (folders.Failure is not null)
        {
            return new GuardDecision.Fail("graph read mail-folders failed");
        }

        return folders.Value!.Any(f => f.Id == destination && !f.Excluded && f.WellKnownName is not ("recoverableitemsdeletions" or "purges"))
            ? GuardDecision.Pass
            : Deny("destination not allowed");
    }

    // --- shared rules ---------------------------------------------------------------------------------------------------

    // Arguments the server would merge into the body or the path beyond the tool's own: refused, named when printable.
    private static GuardDecision.Deny? ExpectArgs(JsonView args, params string[] allowed)
    {
        foreach (var name in args.Keys)
        {
            if (!allowed.Contains(name) && name is not ("includeHeaders" or "excludeResponse" or "confirm"))
            {
                return ArgumentName().IsMatch(name) ? Deny($"unexpected argument {name}") : Deny("unexpected argument");
            }
        }

        return null;
    }

    private GuardDecision.Deny? RequireMailbox(JsonView args) =>
        JsonView.AsciiLower(args.StringOrEmpty("userId")) == JsonView.AsciiLower(configuration.Mailbox) ? null : Deny("only the configured mailbox");

    // (. // []) == []
    private static bool IsEmptyList(JsonElement? element) =>
        !JsonView.IsTruthy(element) || element is { ValueKind: JsonValueKind.Array } a && a.GetArrayLength() == 0;

    // (x // []) is an array
    private static bool IsListOrAbsent(JsonElement? element) => !JsonView.IsTruthy(element) || element is { ValueKind: JsonValueKind.Array };

    private static List<JsonElement> Items(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.Array } a ? [.. a.EnumerateArray()] : [];

    private static GuardDecision.Deny Deny(string reason) => new(reason);

    [GeneratedRegex(@"\A[A-Za-z0-9_$-]{1,40}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ArgumentName();

    [GeneratedRegex(@"\A([^:/]+):/(.+):\z", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex NewFileForm();

    [GeneratedRegex(@"\A[A-Za-z0-9+/]*={0,2}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Base64Shape();
}
