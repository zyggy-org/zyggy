using Zyggy.Core.Memory;
using Zyggy.Core.Verbs;

namespace Zyggy.Core.M365;

/// <summary>
/// <c>zyggy m365 state get &lt;key&gt; [&lt;arg&gt;] | set &lt;key&gt; [&lt;arg&gt;] &lt;value&gt; | reset &lt;key&gt; [&lt;arg&gt;]</c>: the template's
/// <c>state.sh</c> (spec 33 AC-28). Exit 0 · 3 configuration · 4 usage or invalid value. Accepts <c>ZYGGY_HOOKS=off</c>.
/// </summary>
internal sealed class StateVerb(M365VerbContext context) : IM365Verb
{
    private const string Prefix = "m365-state: ";
    private const string Usage = " (usage: zyggy m365 state get <key> [<arg>] | set <key> [<arg>] <value> | reset <key> [<arg>])";

    public async Task<int> RunAsync(IReadOnlyList<string> args, VerbIo io, CancellationToken cancellationToken)
    {
        var parsed = Parse(args);
        if (parsed.Error is not null)
        {
            await io.Error.WriteAsync(Prefix + parsed.Error + "\n").ConfigureAwait(false);
            return 4;
        }

        var memory = MemoryEnvironment.Resolve(context.Environment, context.FindTimeZone);
        if (memory.Error is not null)
        {
            await io.Error.WriteAsync(Prefix + "configuration error: " + memory.Error + "\n").ConfigureAwait(false);
            return 3;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var state = new M365State(new M365Paths(context.Environment), context.Clock);
        switch (parsed.Verb)
        {
            case "get":
                await io.Out.WriteAsync(state.Get(parsed.Entry!) ?? string.Empty).ConfigureAwait(false);
                break;
            case "set":
                try
                {
                    state.Set(parsed.Entry!, parsed.Value!);
                }
                catch (StateDirectoryException ex)
                {
                    await io.Error.WriteAsync(Prefix + ex.Message + "\n").ConfigureAwait(false);
                    return 3;
                }

                break;
            default:
                state.Reset(parsed.Entry!);
                break;
        }

        return 0;
    }

    private static (string? Verb, StateEntry? Entry, string? Value, string? Error) Parse(IReadOnlyList<string> args)
    {
        static (string?, StateEntry?, string?, string?) Fail(string message, bool usage = true) => (null, null, null, message + (usage ? Usage : string.Empty));

        var verb = args.Count > 0 ? args[0] : string.Empty;
        if (verb.Length == 0)
        {
            return Fail("no verb given");
        }

        var rest = args.Skip(1).ToList();
        string? arg = null;
        string? value = null;
        int argCount;
        switch (verb)
        {
            case "get" or "reset":
                if (rest.Count is < 1 or > 2)
                {
                    return Fail($"{verb} needs <key> [<arg>]");
                }

                if (rest.Count == 2)
                {
                    arg = rest[1];
                }

                argCount = rest.Count - 1;
                break;
            case "set":
                if (rest.Count is < 2 or > 3)
                {
                    return Fail("set needs <key> [<arg>] <value>");
                }

                if (rest.Count == 3)
                {
                    arg = rest[1];
                }

                value = rest[^1];
                argCount = rest.Count - 2;
                break;
            default:
                return Fail($"unknown verb '{verb}'");
        }

        if (arg is not null && !M365Grammar.StateArgument().IsMatch(arg))
        {
            return Fail($"'{ShellText.Prefix(arg, 40)}' is not a valid argument");
        }

        var key = rest[0];
        var (entry, error) = Resolve(key, arg, argCount);
        if (error is not null)
        {
            return Fail(error);
        }

        if (value is not null)
        {
            var (valid, expected) = entry!.Grammar switch
            {
                StateGrammar.Iso => (M365Grammar.Iso().IsMatch(value), "an ISO timestamp (YYYY-MM-DDTHH:MM:SSZ)"),
                StateGrammar.Cursor => (M365Grammar.Cursor().IsMatch(value), "an ISO timestamp (YYYY-MM-DDTHH:MM:SSZ), optionally followed by |<item-id>"),
                _ => (M365Grammar.Id().IsMatch(value), "one message id"),
            };
            if (!valid)
            {
                return Fail($"invalid value for {key}: expected {expected}", usage: false);
            }
        }

        return (verb, entry, value, null);
    }

    // resolve_key: the file name and value grammar of <key> [<arg>].
    private static (StateEntry? Entry, string? Error) Resolve(string key, string? arg, int argCount) => key switch
    {
        "mail-watermark" => argCount == 0
            ? (new StateEntry(key, "mail-watermark", StateGrammar.Iso), null)
            : (null, "mail-watermark takes no argument"),
        "backfill-watermark" => argCount == 1
            ? (new StateEntry(key, $"backfill-{arg}.watermark", StateGrammar.Iso), null)
            : (null, "backfill-watermark needs <folder>"),
        "drive-token" => argCount == 1
            ? (new StateEntry(key, $"drive-{arg}.token", StateGrammar.Iso), null)
            : (null, "drive-token needs <drive>"),
        "files-backfill-watermark" => argCount == 1
            ? (new StateEntry(key, $"files-backfill-{arg}.watermark", StateGrammar.Cursor), null)
            : (null, "files-backfill-watermark needs <drive>"),
        "replied" => argCount != 1
            ? (null, "replied needs <date>")
            : M365Grammar.Date().IsMatch(arg!)
                ? (new StateEntry(key, $"replied-{arg}.ids", StateGrammar.Id), null)
                : (null, $"'{arg}' is not a date (YYYY-MM-DD)"),
        _ => (null, $"unknown key '{key}'"),
    };
}
