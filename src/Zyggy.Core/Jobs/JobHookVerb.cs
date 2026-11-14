using System.Text.Json;

using Zyggy.Core.Jobs.Hook;
using Zyggy.Core.M365;
using Zyggy.Core.Verbs;

namespace Zyggy.Core.Jobs;

/// <summary>
/// <c>zyggy hook pre-tool-use --root &lt;dir&gt; --blocked-file &lt;path&gt; [--allow &lt;rule&gt;]…</c>: the <c>PreToolUse</c> hook the
/// job runner adds to every job run (spec 15, founding spec §3 Hooks and §8). Reads Claude Code's hook input on stdin and holds
/// the call to the job's <c>allowed_tools</c>, to writes inside the run directory and to no network program unless allowed.
/// </summary>
/// <remarks>
/// Allow: exit 0, no output. Deny: exit 2, one stderr line <c>zyggy: blocked &lt;tool&gt;: &lt;rule&gt; (&lt;detail&gt;)</c>, and one
/// line <c>&lt;tool&gt;: &lt;rule&gt; (&lt;detail&gt;)</c> appended to the blocked-calls file. It fails closed: bad arguments, bad
/// input and every exception are a deny with the rule <c>hook error</c>; no path returns anything but 0 or 2.
/// </remarks>
/// <param name="ignoreCase">Whether paths compare without case; <see cref="ForThisSystem"/> picks it from the operating system.</param>
public sealed class JobHookVerb(bool ignoreCase)
{
    private const string UnknownTool = "unknown";

    /// <summary>Returns the verb with path case handling of the running operating system (case-insensitive on Windows and macOS).</summary>
    /// <returns>The verb.</returns>
    public static JobHookVerb ForThisSystem() => new(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS());

    /// <summary>Decides one tool call.</summary>
    /// <param name="args">The arguments after <c>hook</c>: <c>pre-tool-use</c> and its options.</param>
    /// <param name="io">The console: the hook input on stdin, the deny line on stderr.</param>
    /// <param name="cancellationToken">Cancels reading the input; a cancelled read is a deny too.</param>
    /// <returns>0 to allow the call, 2 to block it.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="args"/> or <paramref name="io"/> is null.</exception>
    public async Task<int> RunAsync(IReadOnlyList<string> args, VerbIo io, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(io);

        var tool = UnknownTool;
        string? blockedFile = null;
        try
        {
            var (options, argumentError) = HookArguments.Parse(args);
            blockedFile = options?.BlockedFile;

            var input = await io.In.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            using var hook = HookInput.Parse(input, requireToolInput: true);
            if (hook is null)
            {
                return await DenyAsync(io, blockedFile, tool, HookDecision.Deny(HookDecision.HookErrorRule, "hook input is not a PreToolUse call"))
                    .ConfigureAwait(false);
            }

            var root = hook.RootElement;
            tool = root.GetProperty("tool_name").GetString() ?? UnknownTool;
            if (options is null)
            {
                return await DenyAsync(io, blockedFile, tool, HookDecision.Deny(HookDecision.HookErrorRule, argumentError!)).ConfigureAwait(false);
            }

            var cwd = root.TryGetProperty("cwd", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            var decision = new JobHookPolicy(options.Root, options.Rules, ignoreCase).Decide(tool, root.GetProperty("tool_input"), cwd);
            return decision.Allowed ? 0 : await DenyAsync(io, blockedFile, tool, decision).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Fail closed: every unexpected error, a cancelled read included, blocks the call.
        catch (Exception)
#pragma warning restore CA1031
        {
            return await DenyAsync(io, blockedFile, tool, HookDecision.Deny(HookDecision.HookErrorRule, "internal error")).ConfigureAwait(false);
        }
    }

    private static async Task<int> DenyAsync(VerbIo io, string? blockedFile, string tool, HookDecision decision)
    {
        var line = $"{HookDecision.OneLine(tool)}: {decision.Rule} ({decision.Detail})\n";
        try
        {
            await io.Error.WriteAsync("zyggy: blocked " + line).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The deny stands even when stderr is gone.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        if (blockedFile is not null)
        {
            try
            {
                await File.AppendAllTextAsync(blockedFile, line).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // The deny stands even when the blocked-calls file cannot be written.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }

        return 2;
    }

    private sealed record HookArguments(string Root, string BlockedFile, IReadOnlyList<string> Rules)
    {
        public static (HookArguments? Options, string? Error) Parse(IReadOnlyList<string> args)
        {
            if (args.Count == 0)
            {
                return (null, "missing hook name");
            }

            if (args[0] != "pre-tool-use")
            {
                return (null, $"unknown hook '{args[0]}'");
            }

            string? root = null;
            string? blocked = null;
            var rules = new List<string>();
            for (var i = 1; i < args.Count; i++)
            {
                if (args[i] is not ("--root" or "--blocked-file" or "--allow"))
                {
                    return (null, $"unexpected argument '{args[i]}'");
                }

                if (i + 1 >= args.Count)
                {
                    return (null, $"{args[i]} needs a value");
                }

                var value = args[++i];
                switch (args[i - 1])
                {
                    case "--root":
                        root = value;
                        break;
                    case "--blocked-file":
                        blocked = value;
                        break;
                    default:
                        rules.Add(value);
                        break;
                }
            }

            if (root is null)
            {
                return (null, "missing --root");
            }

            if (blocked is null)
            {
                return (null, "missing --blocked-file");
            }

            if (!Path.IsPathFullyQualified(root))
            {
                return (null, "--root must be an absolute path");
            }

            return (new HookArguments(root, blocked, rules), null);
        }
    }
}
