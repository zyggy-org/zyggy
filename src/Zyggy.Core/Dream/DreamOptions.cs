namespace Zyggy.Core.Dream;

/// <summary>
/// The dream pass thresholds (spec 28 Configuration): code defaults, optionally tightened by <c>instance/dream.json</c>. A value
/// above its hard ceiling (or below its minimum) is a configuration error, never silently clamped.
/// </summary>
public sealed record DreamOptions
{
    /// <summary>Gets the most lines one filing call receives.</summary>
    public int BatchMaxLines { get; init; } = 150;

    /// <summary>Gets the smallest batch after repeated failures.</summary>
    public int BatchMinLines { get; init; } = 10;

    /// <summary>Gets the most bytes one filing call receives.</summary>
    public int BatchMaxBytes { get; init; } = 60_000;

    /// <summary>Gets the most filing calls per run.</summary>
    public int MaxBatchesPerRun { get; init; } = 20;

    /// <summary>Gets the timeout of one model call in minutes.</summary>
    public int CallTimeoutMinutes { get; init; } = 15;

    /// <summary>Gets the turn cap of one model call.</summary>
    public int CallMaxTurns { get; init; } = 30;

    /// <summary>Gets the budget cap of one model call in USD.</summary>
    public decimal CallMaxBudgetUsd { get; init; } = 5m;

    /// <summary>Gets the wall-clock cap of a run in minutes.</summary>
    public int RunMaxMinutes { get; init; } = 150;

    /// <summary>Gets the total budget of a run in USD.</summary>
    public decimal RunMaxBudgetUsd { get; init; } = 100m;

    /// <summary>Gets the body-line count above which a file is compressed.</summary>
    public int CompressAboveLines { get; init; } = 300;

    /// <summary>Gets the largest share of a file's lines a compression may remove.</summary>
    public double CompressMaxRemovedRatio { get; init; } = 0.5;

    /// <summary>Gets the most compressions per run.</summary>
    public int MaxCompressionsPerRun { get; init; } = 5;

    /// <summary>Gets the largest share of the touched files' lines one batch may remove.</summary>
    public double BatchMaxRemovedRatio { get; init; } = 0.25;

    /// <summary>Gets the most lines one batch may remove.</summary>
    public int BatchMaxRemovedLines { get; init; } = 40;

    /// <summary>Gets the largest share of all durable lines at run start a run may remove.</summary>
    public double RunMaxRemovedRatio { get; init; } = 0.10;

    /// <summary>Gets the largest share of its body lines an identity file may lose in one batch.</summary>
    public double IdentityMaxShrinkRatio { get; init; } = 0.10;

    /// <summary>Gets the most categories on one side.</summary>
    public int MaxCategoriesPerSide { get; init; } = 12;

    /// <summary>Gets the most categories one run may create.</summary>
    public int MaxNewCategoriesPerRun { get; init; } = 3;

    /// <summary>Gets the days between the last consumption of a closed inbox file and its deletion.</summary>
    public int InboxDeleteGraceDays { get; init; } = 7;

    /// <summary>Gets the age in days after which a daily file is rolled into its month.</summary>
    public int DailyRollupDays { get; init; } = 30;

    /// <summary>Gets the failures at the minimum batch size with the same first line before its lines are quarantined.</summary>
    public int QuarantineAfter { get; init; } = 3;

    /// <summary>Gets the model alias or name; <see langword="null"/> keeps the account default.</summary>
    public string? Model { get; init; }

    /// <summary>Returns the <c>dream.json</c> keys whose value is above its ceiling or below its minimum.</summary>
    /// <returns>The offending keys in table order; empty when valid.</returns>
    public IReadOnlyList<string> Validate()
    {
        var offending = new List<string>();
        void Check(bool ok, string key)
        {
            if (!ok)
            {
                offending.Add(key);
            }
        }

        Check(BatchMaxLines is >= 1 and <= 400, "batchMaxLines");
        Check(BatchMinLines >= 1 && BatchMinLines <= BatchMaxLines, "batchMinLines");
        Check(BatchMaxBytes is >= 1 and <= 200_000, "batchMaxBytes");
        Check(MaxBatchesPerRun is >= 1 and <= 60, "maxBatchesPerRun");
        Check(CallTimeoutMinutes is >= 1 and <= 45, "callTimeoutMinutes");
        Check(CallMaxTurns is >= 1 and <= 80, "callMaxTurns");
        Check(CallMaxBudgetUsd is > 0m and <= 25m, "callMaxBudgetUsd");
        Check(RunMaxMinutes is >= 1 and <= 360, "runMaxMinutes");
        Check(RunMaxBudgetUsd is > 0m and <= 500m, "runMaxBudgetUsd");
        Check(CompressAboveLines is >= 1 and <= 300, "compressAboveLines");
        Check(CompressMaxRemovedRatio is > 0 and <= 0.6, "compressMaxRemovedRatio");
        Check(MaxCompressionsPerRun is >= 0 and <= 20, "maxCompressionsPerRun");
        Check(BatchMaxRemovedRatio is >= 0 and <= 0.4, "batchMaxRemovedRatio");
        Check(BatchMaxRemovedLines is >= 0 and <= 120, "batchMaxRemovedLines");
        Check(RunMaxRemovedRatio is >= 0 and <= 0.2, "runMaxRemovedRatio");
        Check(IdentityMaxShrinkRatio is >= 0 and <= 0.2, "identityMaxShrinkRatio");
        Check(MaxCategoriesPerSide is >= 1 and <= 20, "maxCategoriesPerSide");
        Check(MaxNewCategoriesPerRun is >= 0 and <= 5, "maxNewCategoriesPerRun");
        Check(InboxDeleteGraceDays >= 3, "inboxDeleteGraceDays");
        Check(DailyRollupDays >= 30, "dailyRollupDays");
        Check(QuarantineAfter is >= 1 and <= 5, "quarantineAfter");
        return offending;
    }
}
