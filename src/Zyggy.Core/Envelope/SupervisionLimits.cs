namespace Zyggy.Core.Envelope;

/// <summary>The size limits of the question and decision envelopes (deliverable 18): protocol values, not settings.</summary>
internal static class SupervisionLimits
{
    /// <summary>The most characters a question's body holds.</summary>
    internal const int QuestionMax = 600;

    /// <summary>The most characters a decision's body holds.</summary>
    internal const int DecisionMax = 1200;

    /// <summary>The most characters of a one-line field: <c>default</c>, <c>undo</c>, an option.</summary>
    internal const int LineMax = 200;

    /// <summary>The most options a question lists.</summary>
    internal const int OptionsMax = 5;

    /// <summary>The most evidence items a decision names.</summary>
    internal const int EvidenceMax = 10;

    /// <summary>The most branches a mandate names.</summary>
    internal const int BranchesMax = 5;

    /// <summary>The largest <c>max_jobs</c> of a mandate.</summary>
    internal const int MaxJobsMax = 200;

    /// <summary>The largest <c>max_jobs_per_hour</c> of a mandate.</summary>
    internal const int MaxJobsPerHourMax = 60;

    /// <summary>The largest <c>max_jobs_per_step</c> of a mandate.</summary>
    internal const int MaxJobsPerStepMax = 20;

    /// <summary>The longest a mandate may live after its <c>created</c>.</summary>
    internal static readonly TimeSpan MandateLifetimeMax = TimeSpan.FromDays(7);
}
