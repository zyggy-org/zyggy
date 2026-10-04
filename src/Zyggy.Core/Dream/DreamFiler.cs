using Zyggy.Core.Memory;
using Zyggy.Core.Models;
using Zyggy.Core.Runs;

namespace Zyggy.Core.Dream;

/// <summary>What a run passes to each batch.</summary>
internal sealed record DreamRunContext(MemoryPaths Paths, string StateDirectory, DateOnly RunDate, DreamOptions Options)
{
    /// <summary>The secret patterns every added line, description and alias is scanned with.</summary>
    public SecretPatterns Secrets { get; init; } = SecretPatterns.None;

    /// <summary>Durable files with someone else's uncommitted changes: read-only for the model in this run.</summary>
    public IReadOnlySet<string> CarriedPaths { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Categories created by earlier batches of this run.</summary>
    public int NewCategoriesSoFar { get; init; }
}

/// <summary>Counts of one batch for the run record (never a fact text).</summary>
internal sealed record BatchCounts(
    int Lines,
    int Filed,
    int Merged,
    int Duplicate,
    IReadOnlyDictionary<string, int> Dropped,
    int FilesCreated,
    int FilesEdited,
    int CategoriesCreated);

/// <summary>How a batch ended.</summary>
internal abstract record BatchOutcome;

/// <summary>The batch passed and its changes are in the run's working set.</summary>
internal sealed record BatchAccepted(DreamBatch Batch, DreamProposal Proposal, BatchCounts Counts, decimal? CostUsd, int? Turns, TimeSpan Duration)
    : BatchOutcome;

/// <summary>The proposal broke a rule; nothing of the batch was kept.</summary>
internal sealed record BatchAborted(DreamCheck Check, decimal? CostUsd, int? Turns, TimeSpan Duration) : BatchOutcome
{
    /// <summary>The fact-free detail of the refusal (file, line id, counts or pattern name).</summary>
    public string? Detail { get; init; }
}

/// <summary>The model call failed; nothing of the batch was kept.</summary>
internal sealed record BatchFailed(RunFailureReason Reason, string Detail, decimal? CostUsd, int? Turns, TimeSpan Duration) : BatchOutcome;

/// <summary>
/// Files one batch: renders it as data, runs the filing session (empty run directory, the memory as <c>--add-dir</c>,
/// <c>Read,Grep,Glob</c> only, no MCP, hooks, auto memory or slash commands, no transcript), parses the proposal and applies it to a
/// copy of the working set, which is adopted only when every check passes.
/// </summary>
internal sealed class DreamFiler(IModelRunner model, DreamPrompts prompts, TimeProvider clock)
{
    public async Task<BatchOutcome> FileBatchAsync(
        DreamBatch batch,
        MemorySnapshot snapshot,
        WorkingSet set,
        DreamRunContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(context);
        var options = context.Options;
        var runDirectory = Path.Join(context.StateDirectory, "runs", Ulid.NewUlid().ToString());
        Directory.CreateDirectory(runDirectory);
        var started = clock.GetTimestamp();
        try
        {
            var request = new ModelRunRequest(DreamPrompts.RenderFilingInput(batch, set, context.RunDate), runDirectory,
                TimeSpan.FromMinutes(options.CallTimeoutMinutes))
            {
                AppendSystemPrompt = prompts.FilingPrompt,
                Tools = ["Read", "Grep", "Glob"],
                AdditionalDirectories = [context.Paths.PrincipalDirectory],
                MaxTurns = options.CallMaxTurns,
                MaxBudgetUsd = options.CallMaxBudgetUsd,
                JsonSchema = prompts.FilingSchema,
                Model = options.Model,
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
                return new BatchFailed(RunFailureReason.ClaudeError, ModelFailureDetail.StartFailed, null, null, clock.GetElapsedTime(started));
            }

            if (result.Outcome == ModelRunOutcome.Failed)
            {
                return new BatchFailed(result.Reason ?? RunFailureReason.ClaudeError, result.FailureDetail ?? ModelFailureDetail.IsError,
                    result.CostUsd, result.NumTurns, result.Duration);
            }

            if (DreamProposalParser.Parse(result.StructuredOutput) is not { } proposal)
            {
                return new BatchAborted(DreamCheck.FormatInvalid, result.CostUsd, result.NumTurns, result.Duration) { Detail = "structured output does not match the filing schema" };
            }

            var candidate = set.Clone();
            var applied = ProposalApplier.Apply(proposal, snapshot, candidate, context.RunDate);
            if (DreamChecks.CheckBatch(batch, proposal, set, candidate, applied, context) is { } check)
            {
                return new BatchAborted(check.Check, result.CostUsd, result.NumTurns, result.Duration) { Detail = check.Detail };
            }

            set.Adopt(candidate);
            return new BatchAccepted(batch, proposal, Counts(batch, proposal, applied), result.CostUsd, result.NumTurns, result.Duration);
        }
        finally
        {
            try
            {
                Directory.Delete(runDirectory, recursive: true);
            }
            catch (IOException)
            {
                // A leftover empty run directory under the state directory is harmless.
            }
        }
    }

    private static BatchCounts Counts(DreamBatch batch, DreamProposal proposal, ApplyResult applied)
    {
        int Count(string outcome) => proposal.Dispositions.Count(d => d.Outcome == outcome);
        var dropped = proposal.Dispositions.Where(d => d.Outcome == "dropped")
            .GroupBy(d => d.DropReason ?? "unspecified", StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        return new BatchCounts(batch.Lines.Count, Count("filed"), Count("merged"), Count("duplicate"), dropped, applied.FilesCreated,
            applied.FilesEdited, applied.CategoriesCreated);
    }
}
