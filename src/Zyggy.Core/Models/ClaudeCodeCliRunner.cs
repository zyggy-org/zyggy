using Microsoft.Extensions.Options;

using Zyggy.Core.Processes;
using Zyggy.Core.Runs;

namespace Zyggy.Core.Models;

/// <summary>
/// The v1 <see cref="IModelRunner"/>: runs <c>claude -p</c> through <see cref="IProcessRunner"/> with the spec's Invocation
/// contract, the prompt on standard input, and maps every stream outcome to a result. Only cancellation by the caller throws.
/// </summary>
internal sealed class ClaudeCodeCliRunner(IProcessRunner processes, IOptions<ClaudeCodeOptions> options, TimeProvider clock) : IModelRunner
{
    // Recognised from the result text of a failed run; unconfirmed until the first live run (spec AC-30). A wrong guess only
    // changes the detail token, never the outcome.
    private static readonly string[] AuthMarkers = ["invalid api key", "/login", "not logged in", "authentication", "oauth token", "401"];
    private static readonly string[] RateMarkers = ["usage limit", "rate limit", "rate_limit", "429", "overloaded"];

    private static readonly HashSet<string> KnownErrorSubtypes =
    [
        ModelFailureDetail.ErrorMaxTurns,
        ModelFailureDetail.ErrorMaxBudgetUsd,
        ModelFailureDetail.ErrorMaxStructuredOutputRetries,
        ModelFailureDetail.ErrorDuringExecution,
    ];

    public async Task<ModelRunResult> RunAsync(ModelRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(request.Timeout, TimeSpan.Zero);

        var path = options.Value.Path;
        var reader = new StreamJsonReader(request.MaxCaptureBytes);
        var environment = request.Environment.ToDictionary(kv => kv.Key, kv => (string?)kv.Value);
        environment["CREDENTIALS_DIRECTORY"] = null;
        var spec = new ProcessSpec(path, ClaudeArguments.Build(request), request.WorkingDirectory)
        {
            Environment = environment,
            StandardInput = request.Prompt,
            Timeout = request.Timeout,
            OnStdoutLine = reader.Accept,
            MaxStdoutBytes = request.MaxCaptureBytes,
        };

        var started = clock.GetTimestamp();
        ProcessResult process;
        try
        {
            process = await processes.RunAsync(spec, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Failed(RunFailureReason.ClaudeError, ModelFailureDetail.Canceled, reader, clock.GetElapsedTime(started), null);
        }
#pragma warning disable CA1031 // The seam contract: no failure of the model process reaches the caller as an exception.
        catch (Exception)
#pragma warning restore CA1031
        {
            return Failed(RunFailureReason.ClaudeError, ModelFailureDetail.StartFailed, reader, clock.GetElapsedTime(started), null);
        }

        return Classify(request, path, process, reader);
    }

    private static ModelRunResult Classify(ModelRunRequest request, string path, ProcessResult process, StreamJsonReader reader)
    {
        if (process.StartFailed)
        {
            var detail = Path.IsPathRooted(path) && !File.Exists(path) ? ModelFailureDetail.NotFound : ModelFailureDetail.StartFailed;
            return Failed(RunFailureReason.ClaudeError, detail, reader, process.Duration, process.ExitCode);
        }

        if (process.TimedOut)
        {
            return Failed(RunFailureReason.Timeout, ModelFailureDetail.Timeout, reader, process.Duration, process.ExitCode);
        }

        if (reader.OutputTooLarge || process.StdoutTruncated)
        {
            return Failed(RunFailureReason.ClaudeError, ModelFailureDetail.OutputTooLarge, reader, process.Duration, process.ExitCode);
        }

        if (!reader.SawResult)
        {
            return Failed(RunFailureReason.ClaudeError, ModelFailureDetail.NoResult, reader, process.Duration, process.ExitCode);
        }

        if (reader.ResultUnparseable)
        {
            return Failed(RunFailureReason.ClaudeError, ModelFailureDetail.UnparseableResult, reader, process.Duration, process.ExitCode);
        }

        if (reader.IsError || reader.Subtype?.StartsWith("error", StringComparison.Ordinal) == true)
        {
            return Failed(RunFailureReason.ClaudeError, ErrorDetail(reader), reader, process.Duration, process.ExitCode);
        }

        if (process.ExitCode is { } code and not 0)
        {
            return Failed(RunFailureReason.ClaudeError, ModelFailureDetail.Exit(code), reader, process.Duration, process.ExitCode);
        }

        if (request.JsonSchema is not null && reader.StructuredOutput is null)
        {
            return Failed(RunFailureReason.ClaudeError, ModelFailureDetail.NoStructuredOutput, reader, process.Duration, process.ExitCode);
        }

        return Result(ModelRunOutcome.Succeeded, null, null, reader, process.Duration, process.ExitCode);
    }

    private static string ErrorDetail(StreamJsonReader reader)
    {
        if (reader.Subtype is { } subtype && KnownErrorSubtypes.Contains(subtype))
        {
            return subtype;
        }

        var text = reader.ResultText ?? string.Empty;
        if (AuthMarkers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase)))
        {
            return ModelFailureDetail.Auth;
        }

        return RateMarkers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase))
            ? ModelFailureDetail.RateLimit
            : ModelFailureDetail.IsError;
    }

    private static ModelRunResult Failed(RunFailureReason reason, string detail, StreamJsonReader reader, TimeSpan duration, int? exitCode) =>
        Result(ModelRunOutcome.Failed, reason, detail, reader, duration, exitCode);

    private static ModelRunResult Result(
        ModelRunOutcome outcome,
        RunFailureReason? reason,
        string? detail,
        StreamJsonReader reader,
        TimeSpan duration,
        int? exitCode) =>
        new(
            outcome,
            reason,
            detail,
            reader.ResultText,
            reader.StructuredOutput,
            reader.CostUsd,
            reader.NumTurns,
            duration,
            reader.Model,
            reader.InputTokens,
            reader.OutputTokens,
            exitCode,
            reader.PermissionDenials);
}
