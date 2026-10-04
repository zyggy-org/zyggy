using System.Globalization;

namespace Zyggy.Core.Models;

/// <summary>The closed list of failure detail tokens a <see cref="ModelRunResult"/> carries (logged, never free text).</summary>
public static class ModelFailureDetail
{
    /// <summary>The executable does not exist.</summary>
    public const string NotFound = "not_found";

    /// <summary>The process could not be started for another reason.</summary>
    public const string StartFailed = "start_failed";

    /// <summary>The session produced no result event.</summary>
    public const string NoResult = "no_result";

    /// <summary>The result event could not be parsed.</summary>
    public const string UnparseableResult = "unparseable_result";

    /// <summary>The result reported an error without a known subtype.</summary>
    public const string IsError = "is_error";

    /// <summary>The session hit its turn limit.</summary>
    public const string ErrorMaxTurns = "error_max_turns";

    /// <summary>The session hit its per-session budget.</summary>
    public const string ErrorMaxBudgetUsd = "error_max_budget_usd";

    /// <summary>The model could not produce schema-valid output.</summary>
    public const string ErrorMaxStructuredOutputRetries = "error_max_structured_output_retries";

    /// <summary>The session failed during execution.</summary>
    public const string ErrorDuringExecution = "error_during_execution";

    /// <summary>A schema was given but the successful result carried no structured output.</summary>
    public const string NoStructuredOutput = "no_structured_output";

    /// <summary>Standard output exceeded the capture cap.</summary>
    public const string OutputTooLarge = "output_too_large";

    /// <summary>The CLI is not logged in or its credential was refused.</summary>
    public const string Auth = "auth";

    /// <summary>A rate or usage limit was reached.</summary>
    public const string RateLimit = "rate_limit";

    /// <summary>The session exceeded its timeout and was killed.</summary>
    public const string Timeout = "timeout";

    /// <summary>The session was cancelled from inside the runner, not by the caller.</summary>
    public const string Canceled = "canceled";

    /// <summary>Returns the token for a non-zero exit, for example <c>exit_1</c>.</summary>
    /// <param name="code">The exit code.</param>
    /// <returns><c>exit_</c> followed by the code.</returns>
    public static string Exit(int code) => "exit_" + code.ToString(CultureInfo.InvariantCulture);
}
