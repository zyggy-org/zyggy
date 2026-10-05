using Zyggy.Core.M365.Guard;
using Zyggy.Core.Verbs;

namespace Zyggy.Core.M365;

/// <summary>
/// <c>zyggy m365 log</c>: the PostToolUse hook for the three m365 action tools — <c>m365-log.sh</c> (spec 23 D7, spec 33 AC-22). Appends
/// one body-free row to <c>actions.jsonl</c>; another tool is ignored. A failure to record is exit 2 with one stderr line, which Claude
/// Code shows to Claude (the action already ran). Needs no configuration and no Graph.
/// </summary>
internal sealed class LogVerb(M365VerbContext context) : IM365Verb
{
    private const string Prefix = "m365-log: ";

    public async Task<int> RunAsync(IReadOnlyList<string> args, VerbIo io, CancellationToken cancellationToken)
    {
        try
        {
            if (args.Count > 0)
            {
                return await FailAsync(io, "takes no arguments").ConfigureAwait(false);
            }

            var input = await io.In.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            using var hook = HookInput.Parse(input, requireToolInput: false);
            if (hook is null)
            {
                return await FailAsync(io, "hook input is not a PostToolUse object with tool_name").ConfigureAwait(false);
            }

            if (ActionLog.BuildRow(hook.RootElement, context.Clock.GetUtcNow()) is not { } row)
            {
                return 0;
            }

            var log = new ActionLog(new M365Paths(context.Environment).StateDirectory);
            try
            {
                log.Append(row);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return await FailAsync(io, $"could not append to {log.FilePath}").ConfigureAwait(false);
            }

            return 0;
        }
#pragma warning disable CA1031 // A failure to record is reported, never thrown at Claude Code.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return await FailAsync(io, "internal error").ConfigureAwait(false);
        }
    }

    private static async Task<int> FailAsync(VerbIo io, string message)
    {
        await io.Error.WriteAsync(Prefix + message + "\n").ConfigureAwait(false);
        return 2;
    }
}
