using System.Text.RegularExpressions;

namespace Zyggy.Core.Envelope;

/// <summary>
/// The shape rules of the job field <c>model</c> and of the job and report field <c>job_type</c> (spec 50, founding spec §4). The single
/// source of these rules: the parser applies them, and through it the envelope factory and the submitter's read-back.
/// </summary>
internal static partial class JobModelRules
{
    /// <summary>The rule text a refused <c>model</c> is reported with (never the value).</summary>
    internal const string ModelRule =
        "must be a full Claude Code model id: 1–64 characters of a–z 0–9 . -, starting with a letter, optionally one [label] suffix of a–z 0–9; never an alias";

    /// <summary>The rule text a refused <c>job_type</c> is reported with (never the value).</summary>
    internal const string JobTypeRule = "must be a label of 1–63 characters of a–z 0–9 -";

    // Spec 50 Behavior: the alias words Claude Code resolves differently over time; only a full id can be compared with what ran.
    private static readonly HashSet<string> Aliases = new(StringComparer.Ordinal) { "opus", "sonnet", "haiku", "fable", "default", "best", "opusplan" };

    /// <summary>Whether <paramref name="model"/> has the shape of a job <c>model</c>: a full id, never an alias.</summary>
    /// <param name="model">The field text.</param>
    /// <returns><see langword="true"/> when the id is acceptable.</returns>
    internal static bool IsValidModel(string model)
    {
        Match match = ModelShape().Match(model);
        return match.Success && !Aliases.Contains(match.Groups["id"].Value);
    }

    /// <summary>Whether <paramref name="jobType"/> has the shape of a <c>job_type</c> label.</summary>
    /// <param name="jobType">The field text.</param>
    /// <returns><see langword="true"/> when the label is acceptable.</returns>
    internal static bool IsValidJobType(string jobType) => JobTypeShape().IsMatch(jobType);

    [GeneratedRegex(@"^(?<id>[a-z][a-z0-9.-]{0,63})(\[[a-z0-9]+\])?\z")]
    private static partial Regex ModelShape();

    [GeneratedRegex(@"^[a-z0-9-]{1,63}\z")]
    private static partial Regex JobTypeShape();
}
