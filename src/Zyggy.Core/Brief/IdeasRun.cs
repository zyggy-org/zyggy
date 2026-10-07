using System.Globalization;

using Zyggy.Core.M365;
using Zyggy.Core.Memory;
using Zyggy.Core.Models;

namespace Zyggy.Core.Brief;

/// <summary>The ideas run's answer (parsed, not yet filtered), or why there is none; cost and turns either way.</summary>
internal sealed record IdeasRunResult(IReadOnlyList<IdeaSuggestion>? Suggestions, string? Failure, decimal Cost, int Turns);

/// <summary>
/// The brief's ideas run (spec 35 AC-32) in the dream's read-only shape: a fresh empty working directory under <c>brief/runs/</c>
/// (removed afterwards), <c>Read, Grep, Glob</c> only, the principal's memory as an added directory, no MCP, hooks, auto memory or slash
/// commands, no transcript; the forbidden folders denied in the form the Central probe proved (<c>Read(…)</c> rules also refuse Grep);
/// the prompt embedded in the binary, the input on stdin, the answer in the ideas schema.
/// </summary>
internal sealed class IdeasRun(IModelRunner model, BriefPrompts prompts, BriefPaths paths, MemoryPaths memory)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    /// <summary>The deny rules: Zyggy's config, state and caches, Claude's own directory, the mail-derived and remember inbox files, the dream's state.</summary>
    public IReadOnlyList<string> DenyRules()
    {
        var principal = ClaudeRules.Absolute(memory.PrincipalDirectory);
        return
        [
            "Read(~/.config/zyggy/**)",
            "Read(~/.local/state/zyggy/**)",
            "Read(~/.cache/**)",
            "Read(~/.claude/**)",
            $"Read({principal}/inbox/m365-*)",
            $"Read({principal}/inbox/remember-*)",
            $"Read({principal}/.dream/**)",
            .. LinkedIn.LinkedInRunDeny.Rules,
        ];
    }

    public async Task<IdeasRunResult> RunAsync(string input, int maxTurns, decimal budgetUsd, string? modelName, CancellationToken cancellationToken)
    {
        StateFiles.EnsureDirectory(paths.Directory);
        StateFiles.EnsureDirectory(paths.RunsDirectory);
        var runDirectory = Path.Join(paths.RunsDirectory, Ulid.NewUlid().ToString());
        StateFiles.EnsureDirectory(runDirectory);
        try
        {
            var request = new ModelRunRequest(input, runDirectory, Timeout)
            {
                AppendSystemPrompt = prompts.IdeasPrompt,
                Tools = ["Read", "Grep", "Glob"],
                AdditionalDirectories = [memory.PrincipalDirectory],
                DisallowedTools = DenyRules(),
                MaxTurns = maxTurns,
                MaxBudgetUsd = budgetUsd,
                JsonSchema = prompts.IdeasSchema,
                Model = string.IsNullOrEmpty(modelName) ? null : modelName,
                Isolation = ModelSessionIsolation.NoMcp | ModelSessionIsolation.NoHooks | ModelSessionIsolation.NoAutoMemory
                    | ModelSessionIsolation.NoSlashCommands,
                Environment = new Dictionary<string, string> { ["ZYGGY_HOOKS"] = "off" },
                TranscriptPath = null,
            };

            ModelRunResult result;
            try
            {
                result = await model.RunAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new IdeasRunResult(null, "claude could not start", 0m, 0);
            }

            var cost = result.CostUsd ?? 0m;
            var turns = result.NumTurns ?? 0;
            if (result.Outcome == ModelRunOutcome.Failed)
            {
                return new IdeasRunResult(null, $"claude run failed ({result.FailureDetail ?? ModelFailureDetail.IsError})", cost, turns);
            }

            if (cost > budgetUsd || turns > maxTurns)
            {
                var budget = budgetUsd.ToString(CultureInfo.InvariantCulture);
                var costPart = cost > budgetUsd ? $"cost {cost.ToString("0.00", CultureInfo.InvariantCulture)} > budget {budget}" : $"cost {cost.ToString("0.00", CultureInfo.InvariantCulture)} of budget {budget}";
                var turnsPart = turns > maxTurns ? $"turns {turns} > {maxTurns}" : $"turns {turns} of {maxTurns}";
                return new IdeasRunResult(null, $"over the cap ({costPart}, {turnsPart})", cost, turns);
            }

            var (suggestions, rejection) = IdeasOutput.TryParse(result.StructuredOutput);
            return suggestions is null
                ? new IdeasRunResult(null, $"invalid output ({rejection})", cost, turns)
                : new IdeasRunResult(suggestions, null, cost, turns);
        }
        finally
        {
            try
            {
                Directory.Delete(runDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover empty run directory is removed by the brief's retention.
            }
        }
    }
}
