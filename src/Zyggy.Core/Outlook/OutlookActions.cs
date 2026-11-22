using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Zyggy.Core.Configuration;
using Zyggy.Core.M365;
using Zyggy.Core.Verbs;

namespace Zyggy.Core.Outlook;

/// <summary>
/// The outlook action tools of the work laptop (spec 58 slice 7, AC-10 … AC-13): <c>zyggy outlook send | move</c>, the PreToolUse hook
/// <c>zyggy outlook guard</c> and the Stop hook <c>zyggy outlook settle</c>, over the internal Outlook access and the slice 6 guard.
/// </summary>
/// <remarks>
/// <para>
/// The round trip in an attended Claude Code session: the session runs one plain <c>zyggy outlook send|move …</c> through the Bash tool;
/// the guard hook reads that command (<see cref="OutlookShellCall"/>), runs the guard — not attended, policy record scope,
/// <c>outlook.actions</c>, the send or move rules — and either denies it with the reason and one <c>refused</c> row, or answers
/// <c>ask</c> with the full message (or the mail and the folder) so Claude Code shows the owner one permission prompt with exactly
/// what will happen, whatever the session's allow rules say. The pass is recorded (<see cref="OutlookActionPending"/>). On Allow the
/// tool runs: it takes the pass of exactly this request, runs the guard again, acts once in Outlook and writes one <c>done</c> or
/// <c>failed</c> row; without a pass it refuses. On Deny the tool never runs; the pass becomes one <c>denied</c> row at the session's
/// Stop hook (or once it expired).
/// </para>
/// <para>
/// The tools read the node's own <c>node.json</c> only (no <c>--config</c>). Exit codes: 0 done · 1 draft, item or folder not found
/// in Outlook · 2 usage · 3 configuration · 5 refused · 6 Outlook failed or asked for access. The guard hook prints its decision and
/// exits 0; any failure of its own is exit 2 (Claude Code blocks the call). A row never holds a body.
/// </para>
/// </remarks>
/// <param name="environment">The process environment.</param>
/// <param name="clock">The clock of the rows and passes.</param>
/// <param name="access">Creates the Outlook access (COM in production, a fake in tests).</param>
internal sealed class OutlookActions(IReadOnlyDictionary<string, string?> environment, TimeProvider clock, Func<IOutlookAccess> access)
{
    /// <summary>The verbs this class runs.</summary>
    public static readonly IReadOnlySet<string> Verbs = new HashSet<string>(["send", "move", "guard", "settle"], StringComparer.Ordinal);

    private const string Prefix = "outlook: ";
    private const string GuardPrefix = "outlook-guard: ";
    private TimeProvider Clock => clock;

    private IOutlookAccess CreateAccess() => access();

    private const string Usage = " (usage: zyggy outlook send --to <address>... [--cc <address>...] --subject <text> --body <text> | send --draft <n> | move <reference> --to <folder>)";

    /// <summary>Runs one of <see cref="Verbs"/>.</summary>
    /// <param name="args">The arguments after <c>outlook</c>, the verb first.</param>
    /// <param name="io">The console.</param>
    /// <param name="cancellationToken">Cancels reading stdin.</param>
    /// <returns>The exit code.</returns>
    public async Task<int> RunAsync(IReadOnlyList<string> args, VerbIo io, CancellationToken cancellationToken)
    {
        return args[0] switch
        {
            "guard" => await GuardAsync(args, io, cancellationToken).ConfigureAwait(false),
            "settle" => await SettleAsync(io).ConfigureAwait(false),
            _ => await ActAsync(args, io).ConfigureAwait(false),
        };
    }

    // ---- send | move ----

    private async Task<int> ActAsync(IReadOnlyList<string> args, VerbIo io)
    {
        var call = OutlookActionCall.Parse(args, out string? usage);
        if (call is null)
        {
            await io.Error.WriteAsync(Prefix + usage + Usage + "\n").ConfigureAwait(false);
            return 2;
        }

        if (Load(out string? configError) is not { } context)
        {
            await io.Error.WriteAsync(Prefix + "configuration error: " + configError + "\n").ConfigureAwait(false);
            return 3;
        }

        context.SettleExpired(clock.GetUtcNow());
        var (request, refusal) = context.Resolve(call);
        if (request is null)
        {
            await io.Error.WriteAsync(Prefix + "refused: " + refusal + "\n").ConfigureAwait(false);
            return 5;
        }

        // The pass is taken before the guard runs again, so a call refused now gets one row only (never a later "denied" too).
        bool passed = context.Pending.Take(request.Key, clock.GetUtcNow());
        OutlookGuardDecision decision = context.Check(request);
        if (!decision.Passed)
        {
            await io.Error.WriteAsync(Prefix + "refused: " + decision.Reason + "\n").ConfigureAwait(false);
            return 5;
        }

        if (!passed)
        {
            const string NoPrompt = "no prompt for this call (the outlook guard hook did not pass it)";
            context.Log.Append(request.Row(clock.GetUtcNow(), OutlookActionOutcome.Refused, NoPrompt));
            await io.Error.WriteAsync(Prefix + "refused: " + NoPrompt + "\n").ConfigureAwait(false);
            return 5;
        }

        return await context.DoAsync(request, io).ConfigureAwait(false);
    }

    // ---- the PreToolUse hook ----

    private async Task<int> GuardAsync(IReadOnlyList<string> args, VerbIo io, CancellationToken cancellationToken)
    {
        try
        {
            if (args.Count > 1)
            {
                return await HookFailAsync(io, "takes no arguments").ConfigureAwait(false);
            }

            string input = await io.In.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument? hook = HookInput.Parse(input, requireToolInput: true);
            if (hook is null)
            {
                return await HookFailAsync(io, "hook input is not a PreToolUse object with tool_name and tool_input").ConfigureAwait(false);
            }

            string tool = hook.RootElement.GetProperty("tool_name").GetString()!;
            JsonElement toolInput = hook.RootElement.GetProperty("tool_input");
            string command = toolInput.TryGetProperty("command", out JsonElement c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : string.Empty;
            // Any tool that carries a command naming an action (Bash, PowerShell, a monitor, …) is the guard's; only Bash may pass.
            if (OutlookShellCall.ActionNamed(command) is not { } named)
            {
                return 0;
            }

            if (Load(out string? configError) is not { } context)
            {
                return await HookFailAsync(io, "configuration error: " + configError).ConfigureAwait(false);
            }

            DateTimeOffset now = clock.GetUtcNow();
            context.SettleExpired(now);
            OutlookAction action = named == "move" ? OutlookAction.Move : OutlookAction.Send;
            string? refused = null, rowReason = null;
            OutlookActionCall? call = null;
            if (tool != "Bash")
            {
                refused = "outlook actions run through the Bash tool";
            }
            else if (OutlookShellCall.Read(command) is { Kind: OutlookShellCallKind.Refused } shell)
            {
                refused = "not one plain zyggy outlook call (" + shell.Problem + ")";
            }
            else if ((call = OutlookActionCall.Parse(OutlookShellCall.Read(command).Arguments, out string? usage)) is null)
            {
                // The usage text may quote an argument; the row keeps a fixed reason (a refusal never echoes a request value).
                (refused, rowReason) = (usage, "not a valid outlook call");
            }

            if (refused is not null)
            {
                context.Log.Append(new OutlookActionLogRow(now, action, OutlookActionOutcome.Refused, rowReason ?? refused, null, [], null, null));
                await io.Out.WriteAsync(HookOutput("deny", GuardPrefix + "refused: " + refused)).ConfigureAwait(false);
                return 0;
            }

            var (request, refusal) = context.Resolve(call!);
            OutlookGuardDecision decision = request is null ? new OutlookGuardDecision(refusal) : context.Check(request);
            if (!decision.Passed)
            {
                await io.Out.WriteAsync(HookOutput("deny", GuardPrefix + "refused: " + decision.Reason)).ConfigureAwait(false);
                return 0;
            }

            context.Pending.Add(request!.Key, request.Row(now, OutlookActionOutcome.Denied, null), now);
            await io.Out.WriteAsync(HookOutput("ask", GuardPrefix + context.PromptText(request))).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // Fail closed: every unexpected error blocks the call with one line.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return await HookFailAsync(io, ex is OutlookPromptTimeoutException ? "Outlook asked for access" : "internal error").ConfigureAwait(false);
        }
    }

    // ---- the Stop hook ----

    private async Task<int> SettleAsync(VerbIo io)
    {
        if (Load(out string? configError) is not { } context)
        {
            // A Stop hook that fails must not keep the session from stopping: exit 1, one line.
            await io.Error.WriteAsync(Prefix + "configuration error: " + configError + "\n").ConfigureAwait(false);
            return 1;
        }

        foreach (OutlookActionLogRow row in context.Pending.Settle(clock.GetUtcNow(), all: true))
        {
            context.Log.Append(row);
        }

        return 0;
    }

    private static async Task<int> HookFailAsync(VerbIo io, string message)
    {
        await io.Error.WriteAsync(GuardPrefix + message + "\n").ConfigureAwait(false);
        return 2;
    }

    private static string HookOutput(string decision, string reason)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("hookSpecificOutput");
            writer.WriteString("hookEventName", "PreToolUse");
            writer.WriteString("permissionDecision", decision);
            writer.WriteString("permissionDecisionReason", reason);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    private Context? Load(out string? error)
    {
        error = null;
        string path = NodeConfigurationReader.ResolvePath(null, environment);
        NodeConfigurationResult result = NodeConfigurationReader.Read(path);
        if (result.Configuration is not { } node)
        {
            error = result.Detail;
            return null;
        }

        var settings = OutlookSettings.FromNodeJson(node.Outlook);
        string copy = OutlookPolicy.CopyPath(node.EffectiveNodeDir);
        string? workBoundary = File.Exists(copy) ? File.ReadAllText(copy) : null;
        string stateDirectory = Path.GetDirectoryName(OutlookDrafts.StatePath(node.EffectiveNodeDir, node.Tenant))!;
        return new Context(
            this,
            OutlookPolicy.Evaluate(workBoundary, settings),
            OutlookActionSettings.FromNodeJson(node.Outlook),
            OutlookRunContext.FromEnvironment(environment),
            new OutlookActionLog(stateDirectory),
            new OutlookActionPending(stateDirectory),
            OutlookDrafts.StatePath(node.EffectiveNodeDir, node.Tenant));
    }

    /// <summary>One run's policy, settings, attendance, log, passes and the laptop's drafts.</summary>
    private sealed class Context(
        OutlookActions owner,
        OutlookPolicyDecision policy,
        OutlookActionSettings settings,
        OutlookRunContext run,
        OutlookActionLog log,
        OutlookActionPending pending,
        string draftsPath)
    {
        private IOutlookAccess? _access;

        public OutlookActionLog Log => log;

        public OutlookActionPending Pending => pending;

        private IOutlookAccess Access => _access ??= owner.CreateAccess();

        private OutlookGuard Guard => new(Access, policy, settings, run, log, owner.Clock);

        public void SettleExpired(DateTimeOffset now)
        {
            foreach (OutlookActionLogRow row in pending.Settle(now, all: false))
            {
                log.Append(row);
            }
        }

        // The request of a call: as given, or the kept draft by number (only after the run-level checks, so a job is refused
        // "not attended" before any laptop state is read). A refusal here already wrote its row.
        public (ActionRequest? Request, string? Refusal) Resolve(OutlookActionCall call)
        {
            if (call.Draft is not { } number)
            {
                return (call.Request, null);
            }

            OutlookGuardDecision common = Guard.CheckRun(OutlookAction.Send);
            if (!common.Passed)
            {
                return (null, common.Reason);
            }

            OutlookDraft? draft = OutlookDrafts.Load(draftsPath).Drafts.FirstOrDefault(d => d.Number == number);
            if (draft is null)
            {
                const string NotFound = "draft not found";
                log.Append(new OutlookActionLogRow(owner.Clock.GetUtcNow(), OutlookAction.Send, OutlookActionOutcome.Refused, NotFound, null, [], null, null));
                return (null, NotFound);
            }

            var send = new OutlookSendRequest([draft.To], [], draft.Subject, draft.Text + "\n\n" + draft.Quoted) { InReplyTo = draft.Reference };
            return (ActionRequest.ForSend(send), null);
        }

        public OutlookGuardDecision Check(ActionRequest request) =>
            request.Send is { } send ? Guard.CheckSend(send) : Guard.CheckMove(request.Move!);

        public string PromptText(ActionRequest request)
        {
            if (request.Send is { } send)
            {
                return new StringBuilder()
                    .Append("send this message? Allow sends exactly this, once, as plain text from your default account.\n")
                    .Append("To: ").Append(string.Join(", ", send.To)).Append('\n')
                    .Append("Cc: ").Append(send.Cc.Count == 0 ? "(none)" : string.Join(", ", send.Cc)).Append('\n')
                    .Append("Subject: ").Append(send.Subject).Append("\n\n")
                    .Append(send.Body)
                    .ToString();
            }

            OutlookMoveRequest move = request.Move!;
            OutlookRawItem? item = Access.Find(Access.DefaultStoreId(), move.Reference);
            var text = new StringBuilder("move this mail? Allow moves exactly this one mail; nothing is deleted.\n");
            if (item is not null)
            {
                // The mail's own fields are data: shown on one line each, every control or bidi character a space.
                text.Append("from: ").Append(Flat(item.SenderName)).Append(" <").Append(Flat(item.SenderAddress)).Append(">\n")
                    .Append("subject: ").Append(Flat(item.Subject)).Append('\n')
                    .Append("received: ").Append(item.ReceivedLocal.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append('\n');
            }

            return text.Append("reference: ").Append(move.Reference).Append('\n').Append("to: ").Append(move.Destination).ToString();
        }

        private static string Flat(string text) =>
            string.Concat(text.Select(c => OutlookGuard.HasControl(c.ToString()) ? ' ' : c));

        public async Task<int> DoAsync(ActionRequest request, VerbIo io)
        {
            try
            {
                if (request.Send is { } send)
                {
                    Access.Send(new OutlookOutgoing(send.To, send.Cc, send.Subject, send.Body));
                    log.Append(request.Row(owner.Clock.GetUtcNow(), OutlookActionOutcome.Done, null));
                    await io.Out.WriteAsync(Prefix + "sent to " + string.Join(", ", [.. send.To, .. send.Cc]) + " (a copy is in Sent Items)\n").ConfigureAwait(false);
                    return 0;
                }

                OutlookMoveRequest move = request.Move!;
                if (!Access.Move(Access.DefaultStoreId(), move.Reference, move.Destination))
                {
                    const string Missing = "item or folder not found";
                    log.Append(request.Row(owner.Clock.GetUtcNow(), OutlookActionOutcome.Failed, Missing));
                    await io.Error.WriteAsync(Prefix + "failed: " + Missing + "\n").ConfigureAwait(false);
                    return 1;
                }

                log.Append(request.Row(owner.Clock.GetUtcNow(), OutlookActionOutcome.Done, null));
                await io.Out.WriteAsync(Prefix + "moved to " + move.Destination + "\n").ConfigureAwait(false);
                return 0;
            }
            catch (Exception ex) when (ex is OutlookUnavailableException or OutlookPromptTimeoutException)
            {
                // Never retried another way (spec 58 "Send or move failed").
                string reason = ex is OutlookPromptTimeoutException ? "Outlook asked for access" : OutlookReader.Describe(OutlookRefusal.OutlookNotAvailable);
                log.Append(request.Row(owner.Clock.GetUtcNow(), OutlookActionOutcome.Failed, reason));
                await io.Error.WriteAsync(Prefix + "failed: " + reason + " (" + ex.Message + ")\n").ConfigureAwait(false);
                return 6;
            }
        }
    }
}

/// <summary>One send or move request with its key: the SHA-256 of every field, so a pass is for exactly this request.</summary>
/// <param name="Send">The send, or <see langword="null"/>.</param>
/// <param name="Move">The move, or <see langword="null"/>.</param>
/// <param name="Key">The request's key.</param>
internal sealed record ActionRequest(OutlookSendRequest? Send, OutlookMoveRequest? Move, string Key)
{
    /// <summary>Wraps a send.</summary>
    /// <param name="send">The send.</param>
    /// <returns>The request.</returns>
    public static ActionRequest ForSend(OutlookSendRequest send) => new(send, null, KeyOf(w =>
    {
        w.WriteString("action", "send");
        Strings(w, "to", send.To);
        Strings(w, "cc", send.Cc);
        w.WriteString("subject", send.Subject);
        w.WriteString("body", send.Body);
        Strings(w, "bcc", send.Bcc);
        w.WriteString("format", send.BodyFormat.ToString());
        Strings(w, "attachments", send.Attachments);
        w.WriteString("account", send.SendingAccount);
        w.WriteString("sender", send.Sender);
        Strings(w, "replyTo", send.ReplyTo);
        w.WriteString("inReplyTo", send.InReplyTo);
    }));

    /// <summary>Wraps a move.</summary>
    /// <param name="move">The move.</param>
    /// <returns>The request.</returns>
    public static ActionRequest ForMove(OutlookMoveRequest move) => new(null, move, KeyOf(w =>
    {
        w.WriteString("action", "move");
        w.WriteString("reference", move.Reference);
        w.WriteString("destination", move.Destination);
        w.WriteString("store", move.Store);
        w.WriteBoolean("permanent", move.Permanent);
    }));

    /// <summary>The log row of this request: the recipients and subject of a send, the reference and destination of a move; never the body.</summary>
    /// <param name="time">When the call ended.</param>
    /// <param name="outcome">How it ended.</param>
    /// <param name="reason">The reason, if any.</param>
    /// <returns>The row.</returns>
    public OutlookActionLogRow Row(DateTimeOffset time, OutlookActionOutcome outcome, string? reason) =>
        Send is { } send
            ? new OutlookActionLogRow(time, OutlookAction.Send, outcome, reason, send.InReplyTo, [.. send.To, .. send.Cc], send.Subject, null)
            : new OutlookActionLogRow(time, OutlookAction.Move, outcome, reason, Move!.Reference, [], null, Move.Destination);

    private static void Strings(Utf8JsonWriter writer, string name, IReadOnlyList<string> values)
    {
        writer.WriteStartArray(name);
        foreach (string value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static string KeyOf(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        return Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
    }
}

/// <summary>A parsed <c>send</c> or <c>move</c> call: a request, or a draft number to send.</summary>
/// <param name="Request">The request as given, or <see langword="null"/> for a draft.</param>
/// <param name="Draft">The draft number of <c>send --draft</c>, or <see langword="null"/>.</param>
internal sealed record OutlookActionCall(ActionRequest? Request, int? Draft)
{
    private static readonly HashSet<string> SendLists = new(["--to", "--cc", "--bcc", "--attach", "--reply-to"], StringComparer.Ordinal);
    private static readonly HashSet<string> SendValues = new(["--subject", "--body", "--from", "--account", "--draft"], StringComparer.Ordinal);

    /// <summary>Parses the arguments after <c>outlook</c>: <c>send …</c> or <c>move …</c>.</summary>
    /// <param name="args">The arguments, the verb first.</param>
    /// <param name="error">The usage error, when the result is <see langword="null"/>.</param>
    /// <returns>The call, or <see langword="null"/>.</returns>
    public static OutlookActionCall? Parse(IReadOnlyList<string> args, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        error = null;
        if (args.Count == 0 || args[0] is not ("send" or "move"))
        {
            error = "not a send or a move";
            return null;
        }

        if (args.Contains("--config"))
        {
            error = "the outlook actions read the node's own node.json (no --config)";
            return null;
        }

        return args[0] == "send" ? ParseSend(args, out error) : ParseMove(args, out error);
    }

    private static OutlookActionCall? ParseSend(IReadOnlyList<string> args, out string? error)
    {
        error = null;
        var lists = SendLists.ToDictionary(k => k, _ => new List<string>(), StringComparer.Ordinal);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var html = false;
        for (var i = 1; i < args.Count; i++)
        {
            string arg = args[i];
            if (arg == "--html")
            {
                html = true;
                continue;
            }

            if (!SendLists.Contains(arg) && !SendValues.Contains(arg))
            {
                error = $"unexpected argument '{ShellText.Prefix(arg, 40)}'";
                return null;
            }

            if (i + 1 >= args.Count)
            {
                error = arg + " needs a value";
                return null;
            }

            string value = args[++i];
            if (SendLists.Contains(arg))
            {
                lists[arg].Add(value);
            }
            else if (!values.TryAdd(arg, value))
            {
                error = arg + " is given twice";
                return null;
            }
        }

        if (values.TryGetValue("--draft", out string? draft))
        {
            if (values.Count > 1 || html || lists.Values.Any(l => l.Count > 0))
            {
                error = "send --draft <n> takes no other option (the draft is the whole message)";
                return null;
            }

            if (!int.TryParse(draft, NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number < 1)
            {
                error = "--draft takes a draft number (1, 2, …)";
                return null;
            }

            return new OutlookActionCall(null, number);
        }

        if (!values.TryGetValue("--subject", out string? subject) || !values.TryGetValue("--body", out string? body))
        {
            error = "send needs --subject and --body (or --draft <n>)";
            return null;
        }

        var send = new OutlookSendRequest(lists["--to"], lists["--cc"], subject, body)
        {
            Bcc = lists["--bcc"],
            Attachments = lists["--attach"],
            ReplyTo = lists["--reply-to"],
            BodyFormat = html ? OutlookBodyFormat.Html : OutlookBodyFormat.PlainText,
            Sender = values.GetValueOrDefault("--from"),
            SendingAccount = values.GetValueOrDefault("--account"),
        };
        return new OutlookActionCall(ActionRequest.ForSend(send), null);
    }

    private static OutlookActionCall? ParseMove(IReadOnlyList<string> args, out string? error)
    {
        error = null;
        string? reference = null, destination = null, store = null;
        var permanent = false;
        for (var i = 1; i < args.Count; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--permanent":
                    permanent = true;
                    continue;
                case "--to" or "--store":
                    if (i + 1 >= args.Count || (arg == "--to" ? destination : store) is not null)
                    {
                        error = arg + " needs one value";
                        return null;
                    }

                    if (arg == "--to")
                    {
                        destination = args[++i];
                    }
                    else
                    {
                        store = args[++i];
                    }

                    continue;
                default:
                    if (reference is not null || arg.StartsWith("--", StringComparison.Ordinal))
                    {
                        error = $"unexpected argument '{ShellText.Prefix(arg, 40)}'";
                        return null;
                    }

                    reference = arg;
                    continue;
            }
        }

        if (reference is null || destination is null)
        {
            error = "move needs <reference> --to <folder>";
            return null;
        }

        return new OutlookActionCall(ActionRequest.ForMove(new OutlookMoveRequest(reference, destination) { Store = store, Permanent = permanent }), null);
    }
}
