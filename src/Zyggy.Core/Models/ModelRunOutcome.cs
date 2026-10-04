namespace Zyggy.Core.Models;

/// <summary>Whether a model session succeeded.</summary>
public enum ModelRunOutcome
{
    /// <summary>The session ended with a successful result (and structured output when a schema was given).</summary>
    Succeeded,

    /// <summary>The session failed; <see cref="ModelRunResult.Reason"/> and <see cref="ModelRunResult.FailureDetail"/> say why.</summary>
    Failed,
}
