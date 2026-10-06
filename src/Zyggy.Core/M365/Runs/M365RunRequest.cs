using System.Globalization;
using System.Text.Json;

using Zyggy.Core.M365.Tools;
using Zyggy.Core.Models;

namespace Zyggy.Core.M365.Runs;

/// <summary>The three unattended m365 model runs.</summary>
internal enum M365RunKind
{
    Brief,
    MailBackfill,
    FilesBackfill,
}

/// <summary>The outcome of a run verb: its exit code and its stdout and stderr lines, in order.</summary>
internal sealed record RunOutcome(int Exit, IReadOnlyList<string> StdoutLines, IReadOnlyList<string> StderrLines);

/// <summary>
/// The model request of an m365 run (spec 33 Contracts): the prompt on stdin (decision 7), the checkout as working directory, the run's
/// allow and deny lists from the tool partition, its caps and model from <c>instance/m365.json</c>, <c>ZYGGY_HOOKS=off</c>, the run
/// directory and the unit's <c>CREDENTIALS_DIRECTORY</c> (for the headersHelper) as the only additions to the environment, the checkout's
/// <c>.mcp.json</c> through <c>--mcp-config</c> (so that helper gets them), a 2-hour limit and a 64 MiB capture (Assumption 7).
/// </summary>
internal static class M365RunRequest
{
    public static ModelRunRequest For(M365RunKind kind, string prompt, M365Environment instance, M365Configuration configuration, M365ToolPartition partition, string? runDirectory)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(partition);
        var (section, budgetKey, allow, deny) = kind switch
        {
            M365RunKind.Brief => ("brief", "budget_usd", partition.BriefAllow, partition.BriefDeny),
            M365RunKind.MailBackfill => ("mail_backfill", "budget_usd_per_batch", partition.MailBackfillAllow, partition.MailBackfillDeny),
            _ => ("files_backfill", "budget_usd_per_batch", partition.FilesBackfillAllow, partition.FilesBackfillDeny),
        };
        var block = configuration.Root.GetProperty(section);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal) { ["ZYGGY_HOOKS"] = "off" };
        if (runDirectory is not null)
        {
            environment["ZYGGY_M365_RUN_DIR"] = runDirectory;
        }

        // Claude Code's headersHelper (`zyggy m365 auth-header`) mints the token from the unit's key copy (D8), as the shell's run did.
        if (instance.Paths.CredentialsDirectory is { } credentials)
        {
            environment["CREDENTIALS_DIRECTORY"] = credentials;
        }

        var model = block.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String && m.GetString()!.Length > 0 ? m.GetString() : null;
        var brief = kind == M365RunKind.Brief;
        if (brief)
        {
            // Spec 35 AC-45/AC-46: the mail run reads its run directory, never memory; it gets no auto memory and answers in the schema.
            allow = runDirectory is null ? allow : [.. allow, $"Read({runDirectory}/**)"];
            deny = [.. deny, $"Read(//{Path.Join(instance.Checkout, "memory").Replace('\\', '/')}/**)"];
        }

        return new ModelRunRequest(prompt, instance.Checkout, TimeSpan.FromMinutes(brief ? 30 : 120))
        {
            AllowedTools = allow,
            DisallowedTools = deny,
            MaxTurns = (int)block.GetProperty("max_turns").GetDouble(),
            MaxBudgetUsd = decimal.Parse(block.GetProperty(budgetKey).GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture),
            Model = model,
            Environment = environment,
            Isolation = brief ? ModelSessionIsolation.NoAutoMemory : ModelSessionIsolation.None,
            JsonSchema = brief ? new Brief.BriefPrompts().MailSchema : null,
            McpConfig = Path.Join(instance.Checkout, ".mcp.json"),
            MaxCaptureBytes = 64 * 1024 * 1024,
        };
    }
}
