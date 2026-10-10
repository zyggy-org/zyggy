using Zyggy.Core.Memory;
using Zyggy.Core.Models;
using Zyggy.Core.Runs;

namespace Zyggy.Core.Dream;

/// <summary>How one compression ended.</summary>
internal sealed record CompressionOutcome(string Path, DreamCheck? Rejected, RunFailureReason? Failure, string? Detail, decimal? CostUsd)
{
    public bool Accepted => Rejected is null && Failure is null;
}

/// <summary>
/// AC-16: one model call per memory file over <see cref="DreamOptions.CompressAboveLines"/> body lines, at most
/// <see cref="DreamOptions.MaxCompressionsPerRun"/> per run; the result is checked and applied to the working set, or rejected with
/// <see cref="DreamCheck.CompressRejected"/>.
/// </summary>
internal sealed class Compressor(IModelRunner model, DreamPrompts prompts)
{
    public async Task<IReadOnlyList<CompressionOutcome>> CompressAsync(WorkingSet set, DreamRunContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(context);
        var options = context.Options;
        var candidates = set.Paths
            .Where(p => context.Paths.TryResolve(p) is { Succeeded: true, Area: MemoryArea.Durable })
            .Where(p => !context.CarriedPaths.Contains(p))
            .Where(p => DreamChecks.FactLines(set.Text(p)).Count > options.CompressAboveLines)
            .Order(StringComparer.Ordinal)
            .Take(options.MaxCompressionsPerRun)
            .ToList();

        var outcomes = new List<CompressionOutcome>();
        foreach (var path in candidates)
        {
            outcomes.Add(await CompressOneAsync(path, set, context, cancellationToken).ConfigureAwait(false));
        }

        return outcomes;
    }

    private async Task<CompressionOutcome> CompressOneAsync(string path, WorkingSet set, DreamRunContext context, CancellationToken cancellationToken)
    {
        var options = context.Options;
        var file = MemoryFileReader.Parse(set.Text(path)!);
        var original = file.BodyLines.Where(l => l.StartsWith("- ", StringComparison.Ordinal)).ToList();
        var runDirectory = Path.Join(context.StateDirectory, "runs", Ulid.NewUlid().ToString());
        Directory.CreateDirectory(runDirectory);
        try
        {
            var request = new ModelRunRequest(DreamPrompts.RenderCompressionInput(path, original), runDirectory, TimeSpan.FromMinutes(options.CallTimeoutMinutes))
            {
                AppendSystemPrompt = prompts.CompressionPrompt,
                Tools = ["Read", "Grep", "Glob"],
                AdditionalDirectories = [context.Paths.PrincipalDirectory],
                MaxTurns = options.CallMaxTurns,
                MaxBudgetUsd = options.CallMaxBudgetUsd,
                JsonSchema = prompts.CompressionSchema,
                Model = options.Model,
                DisallowedTools = DreamRunDeny.Rules(context.Paths),
                Isolation = ModelSessionIsolation.NoMcp | ModelSessionIsolation.NoHooks | ModelSessionIsolation.NoAutoMemory
                    | ModelSessionIsolation.NoSlashCommands,
                Environment = new Dictionary<string, string> { ["ZYGGY_HOOKS"] = "off" },
            };

            ModelRunResult result;
            try
            {
                result = await model.RunAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new CompressionOutcome(path, null, RunFailureReason.ClaudeError, ModelFailureDetail.StartFailed, null);
            }

            if (result.Outcome == ModelRunOutcome.Failed)
            {
                return new CompressionOutcome(path, null, result.Reason ?? RunFailureReason.ClaudeError, result.FailureDetail, result.CostUsd);
            }

            var proposal = DreamProposalParser.ParseCompression(result.StructuredOutput);
            if (proposal is null || !IsValid(proposal, path, original, context))
            {
                return new CompressionOutcome(path, DreamCheck.CompressRejected, null, null, result.CostUsd);
            }

            var compressed = file with
            {
                BodyLines = proposal.Lines,
                Description = proposal.Description ?? file.Description,
                Updated = context.RunDate,
                HasFrontMatter = true,
                Name = file.Name ?? Path.GetFileNameWithoutExtension(path),
            };
            set.Write(path, MemoryFileWriter.Render(compressed));
            return new CompressionOutcome(path, null, null, null, result.CostUsd);
        }
        finally
        {
            try
            {
                Directory.Delete(runDirectory, recursive: true);
            }
            catch (IOException)
            {
                // A leftover empty run directory is harmless.
            }
        }
    }

    private static bool IsValid(CompressionProposal proposal, string path, List<string> original, DreamRunContext context)
    {
        var options = context.Options;
        if (proposal.Path != path || proposal.Lines.Count > options.CompressAboveLines)
        {
            return false;
        }

        if (proposal.Description is { } description && (description.Trim().Length == 0 || description.Length >= 150))
        {
            return false;
        }

        var originalSet = original.ToHashSet(StringComparer.Ordinal);
        foreach (var line in proposal.Lines)
        {
            var parsed = MemoryLine.Parse(line);
            if (parsed.Tag == MemoryTag.Other || parsed.Date is null || parsed.TooLong
                || (parsed.Tag == MemoryTag.Observed && parsed.Provenance.Count == 0))
            {
                return false;
            }

            if (!originalSet.Contains(line) && (context.Secrets.TryMatch(line, out _) || ContactDetailPatterns.Contains(line)))
            {
                return false;
            }
        }

        var kept = proposal.Lines.GroupBy(l => l, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var removed = new List<string>();
        foreach (var group in original.GroupBy(l => l, StringComparer.Ordinal))
        {
            var missing = group.Count() - (kept.TryGetValue(group.Key, out var n) ? n : 0);
            removed.AddRange(Enumerable.Repeat(group.Key, Math.Max(0, missing)));
        }

        if (original.Count > 0 && (double)removed.Count / original.Count > options.CompressMaxRemovedRatio)
        {
            return false;
        }

        // Every removed [stated] line is merged into a kept line or expired.
        var keptSet = proposal.Lines.ToHashSet(StringComparer.Ordinal);
        return removed.Where(l => MemoryLine.Parse(l).Tag == MemoryTag.Stated).All(line => proposal.Removed.Any(r =>
            r.Old == line && (r.Reason == "expired" || (r.Reason == "merged" && r.Into is not null && keptSet.Contains(r.Into)))));
    }
}
