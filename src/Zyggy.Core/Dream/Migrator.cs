using System.Text;

using Zyggy.Core.Memory;
using Zyggy.Core.Models;
using Zyggy.Core.Runs;

namespace Zyggy.Core.Dream;

/// <summary>How the one-time layout migration ended.</summary>
internal sealed record MigrationOutcome(DreamCheck? Rejected, RunFailureReason? Failure, string? Detail, decimal? CostUsd, int Moves, int CategoriesCreated)
{
    public bool Accepted => Rejected is null && Failure is null;
}

/// <summary>
/// AC-27: the first run on the 27 layout (root <c>areas/</c>, <c>people/</c>, <c>topics/</c>) only migrates. The model proposes a side
/// and category per legacy file; .NET moves every legacy file exactly once, byte-identical and under the same file name, creates the
/// <c>_index.md</c> files of the initial and new categories, and removes empty placeholder files — or refuses the whole migration.
/// </summary>
internal sealed class Migrator(IModelRunner model, DreamPrompts prompts)
{
    private static readonly string[] InitialCategories = ["areas", "people", "topics"];

    public async Task<MigrationOutcome> MigrateAsync(MemorySnapshot snapshot, WorkingSet set, DreamRunContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(context);
        var paths = snapshot.Paths;
        var legacy = snapshot.Files.Values.Where(f => paths.TryResolve(f.RelativePath) is { Succeeded: true, Area: MemoryArea.Legacy })
            .OrderBy(f => f.RelativePath, StringComparer.Ordinal)
            .ToList();
        var markdown = legacy.Where(f => f.RelativePath.EndsWith(".md", StringComparison.Ordinal) && f.RelativePath.Count(c => c == '/') == 1).ToList();
        var others = legacy.Except(markdown).ToList();
        if (others.Any(f => f.Bytes.Length > 0))
        {
            return new MigrationOutcome(DreamCheck.MigrationRejected, null, "non_markdown_legacy_file", null, 0, 0);
        }

        var options = context.Options;
        var runDirectory = Path.Join(context.StateDirectory, "runs", Ulid.NewUlid().ToString());
        Directory.CreateDirectory(runDirectory);
        ModelRunResult result;
        try
        {
            var listing = markdown.Select(f => (f.RelativePath, $"{f.Parsed?.Name ?? "(no name)"} — {f.Parsed?.Description ?? "(no description)"}"));
            var request = new ModelRunRequest(DreamPrompts.RenderMigrationInput(listing), runDirectory, TimeSpan.FromMinutes(options.CallTimeoutMinutes))
            {
                AppendSystemPrompt = prompts.MigrationPrompt,
                Tools = ["Read", "Grep", "Glob"],
                AdditionalDirectories = [paths.PrincipalDirectory],
                MaxTurns = options.CallMaxTurns,
                MaxBudgetUsd = options.CallMaxBudgetUsd,
                JsonSchema = prompts.MigrationSchema,
                Model = options.Model,
                DisallowedTools = LinkedIn.LinkedInRunDeny.Rules,
                Isolation = ModelSessionIsolation.NoMcp | ModelSessionIsolation.NoHooks | ModelSessionIsolation.NoAutoMemory
                    | ModelSessionIsolation.NoSlashCommands,
                Environment = new Dictionary<string, string> { ["ZYGGY_HOOKS"] = "off" },
            };
            try
            {
                result = await model.RunAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new MigrationOutcome(null, RunFailureReason.ClaudeError, ModelFailureDetail.StartFailed, null, 0, 0);
            }
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

        if (result.Outcome == ModelRunOutcome.Failed)
        {
            return new MigrationOutcome(null, result.Reason ?? RunFailureReason.ClaudeError, result.FailureDetail, result.CostUsd, 0, 0);
        }

        var proposal = DreamProposalParser.ParseMigration(result.StructuredOutput);
        var candidate = set.Clone();
        var created = proposal is null ? -1 : Apply(proposal, markdown, others, snapshot, candidate, context);
        if (created < 0)
        {
            return new MigrationOutcome(DreamCheck.MigrationRejected, null, null, result.CostUsd, 0, 0);
        }

        set.Adopt(candidate);
        return new MigrationOutcome(null, null, null, result.CostUsd, markdown.Count, created);
    }

    /// <summary>Checks and applies the moves; returns the number of new categories, or -1 when the proposal is refused.</summary>
    private static int Apply(MigrationProposal proposal, List<SnapshotFile> markdown, List<SnapshotFile> others, MemorySnapshot snapshot,
        WorkingSet set, DreamRunContext context)
    {
        var paths = snapshot.Paths;
        var legacy = markdown.ToDictionary(f => f.RelativePath, StringComparer.Ordinal);
        var newCategories = proposal.NewCategories.Select(n => (n.Side, n.Name)).ToHashSet();
        if (proposal.NewCategories.Any(n => !MemorySideWire.TryFromWire(n.Side, out _) || !CategoryName.TryParse(n.Name, out _)
                || n.Description.Trim().Length == 0 || n.Description.Length >= 150)
            || newCategories.Count != proposal.NewCategories.Count
            || newCategories.Count > context.Options.MaxNewCategoriesPerRun
            || proposal.Moves.Count != legacy.Count
            || proposal.Moves.Select(m => m.From).Distinct(StringComparer.Ordinal).Count() != legacy.Count
            || proposal.Moves.Select(m => m.To).Distinct(StringComparer.Ordinal).Count() != legacy.Count)
        {
            return -1;
        }

        var usedCategories = new HashSet<(string Side, string Name)>();
        var slugs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var move in proposal.Moves)
        {
            if (!legacy.TryGetValue(move.From, out var file)
                || paths.TryResolve(move.To) is not { Succeeded: true, Area: MemoryArea.Durable } target
                || target.RelativePath != move.To
                || Path.GetFileName(move.To) != Path.GetFileName(move.From)
                || snapshot.Files.ContainsKey(move.To)
                || !slugs.Add(Path.GetFileName(move.To))
                || !Encoding.UTF8.GetBytes(file.Text).AsSpan().SequenceEqual(file.Bytes))
            {
                return -1;
            }

            var segments = move.To.Split('/');
            var category = (segments[0], segments[1]);
            if (!InitialCategories.Contains(segments[1]) && !newCategories.Contains(category))
            {
                return -1;
            }

            usedCategories.Add(category);
            set.Write(move.To, file.Text);
            set.Delete(move.From);
        }

        if (newCategories.Any(n => !usedCategories.Contains(n))
            || Enum.GetValues<MemorySide>().Any(side => InitialCategories.Length + newCategories.Count(n => n.Side == MemorySideWire.ToWire(side))
                > context.Options.MaxCategoriesPerSide))
        {
            return -1;
        }

        foreach (var placeholder in others)
        {
            set.Delete(placeholder.RelativePath);
        }

        foreach (var side in Enum.GetValues<MemorySide>().Select(MemorySideWire.ToWire))
        {
            foreach (var name in InitialCategories)
            {
                WriteIndex(set, side, name, InitialDescription(side, name), context.RunDate);
            }
        }

        foreach (var category in proposal.NewCategories)
        {
            WriteIndex(set, category.Side, category.Name, category.Description, context.RunDate);
        }

        return newCategories.Count;
    }

    private static void WriteIndex(WorkingSet set, string side, string name, string description, DateOnly runDate)
    {
        var path = $"{side}/{name}/_index.md";
        if (!set.Exists(path))
        {
            set.Write(path, MemoryFileWriter.Render(new MemoryFile(name, description, [], runDate, new Dictionary<string, string>(), true, [])));
        }
    }

    // The founding spec §7 meanings of the initial categories.
    private static string InitialDescription(string side, string name)
    {
        var life = side == "private" ? "private life" : "professional life";
        return name switch
        {
            "areas" => $"Projects and responsibilities in the owner's {life}",
            "people" => $"People in the owner's {life}",
            _ => $"Habits, interests and recurring subjects in the owner's {life}",
        };
    }
}
