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
/// allow and deny lists from the tool partition, its caps and model from <c>instance/m365.json</c>, <c>ZYGGY_HOOKS=off</c> and the run
/// directory as the only additions to the environment, a 2-hour limit and a 64 MiB capture (Assumption 7).
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

        var model = block.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String && m.GetString()!.Length > 0 ? m.GetString() : null;
        return new ModelRunRequest(prompt, instance.Checkout, TimeSpan.FromMinutes(120))
        {
            AllowedTools = allow,
            DisallowedTools = deny,
            MaxTurns = (int)block.GetProperty("max_turns").GetDouble(),
            MaxBudgetUsd = decimal.Parse(block.GetProperty(budgetKey).GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture),
            Model = model,
            Environment = environment,
            Isolation = ModelSessionIsolation.None,
            MaxCaptureBytes = 64 * 1024 * 1024,
        };
    }
}
