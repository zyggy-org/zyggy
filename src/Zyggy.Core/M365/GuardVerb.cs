using System.Text.Json;

using Zyggy.Core.M365.Guard;
using Zyggy.Core.Verbs;

namespace Zyggy.Core.M365;

/// <summary>
/// <c>zyggy m365 guard</c>: the PreToolUse hook for the three m365 action tools — <c>m365-guard.sh</c> (spec 23 D7, spec 33 AC-20, AC-21).
/// stdin the hook JSON; stdout nothing (the ask rule prompts) or the deny line; exit 0. Any failure of its own — an argument, bad input,
/// configuration, a Graph read — is exit 2 with one stderr line: Claude Code blocks the call. Never prints a token, a body or content.
/// Runs whatever <c>ZYGGY_HOOKS</c> says.
/// </summary>
internal sealed class GuardVerb(M365VerbContext context) : IM365Verb
{
    private const string Prefix = "m365-guard: ";

    public async Task<int> RunAsync(IReadOnlyList<string> args, VerbIo io, CancellationToken cancellationToken)
    {
        try
        {
            return await DecideAsync(args, io, cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Fail closed: every unexpected error blocks the call with one line.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return await FailAsync(io, "internal error").ConfigureAwait(false);
        }
    }

    private async Task<int> DecideAsync(IReadOnlyList<string> args, VerbIo io, CancellationToken cancellationToken)
    {
        if (args.Count > 0)
        {
            return await FailAsync(io, "takes no arguments").ConfigureAwait(false);
        }

        var input = await io.In.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        if (HookInput.Parse(input, requireToolInput: true) is not { } hook)
        {
            return await FailAsync(io, "hook input is not a PreToolUse object with tool_name and tool_input").ConfigureAwait(false);
        }

        using (hook)
        {
            var root = hook.RootElement;
            if (GuardPolicy.ActionOf(root.GetProperty("tool_name").GetString()!) is not { } action)
            {
                return 0;
            }

            var (session, _, error) = M365Session.Load(context, baseOnly: false);
            if (session is null)
            {
                return await FailAsync(io, error!).ConfigureAwait(false);
            }

            if (session.Warning is not null)
            {
                await io.Error.WriteAsync(session.Warning + "\n").ConfigureAwait(false);
            }

            using var graph = session.CreateGraph(context);
            var decision = await new GuardPolicy(session.Configuration, graph.Reader)
                .EvaluateAsync(action, root.GetProperty("tool_input"), cancellationToken).ConfigureAwait(false);
            switch (decision)
            {
                case GuardDecision.Deny deny:
                    await io.Out.WriteAsync(GuardOutput.Deny(deny.Reason)).ConfigureAwait(false);
                    return 0;
                case GuardDecision.Fail fail:
                    return await FailAsync(io, fail.Message).ConfigureAwait(false);
                default:
                    return 0;
            }
        }
    }

    private static async Task<int> FailAsync(VerbIo io, string message)
    {
        await io.Error.WriteAsync(Prefix + message + "\n").ConfigureAwait(false);
        return 2;
    }
}

/// <summary>A Claude Code hook's stdin: an object with a string <c>tool_name</c> (and an object <c>tool_input</c> for PreToolUse).</summary>
internal static class HookInput
{
    public static JsonDocument? Parse(string input, bool requireToolInput)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(input);
        }
        catch (JsonException)
        {
            return null;
        }

        var root = document.RootElement;
        var valid = root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("tool_name", out var tool) && tool.ValueKind == JsonValueKind.String
            && (!requireToolInput || (root.TryGetProperty("tool_input", out var toolInput) && toolInput.ValueKind == JsonValueKind.Object));
        if (!valid)
        {
            document.Dispose();
            return null;
        }

        return document;
    }
}
