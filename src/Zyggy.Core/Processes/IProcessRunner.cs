namespace Zyggy.Core.Processes;

/// <summary>
/// Runs an external program without a shell. The single path by which Zyggy starts <c>git</c> and <c>claude</c>
/// (founding spec §9); tests substitute it.
/// </summary>
public interface IProcessRunner
{
    /// <summary>Runs the program described by <paramref name="spec"/> to completion, its timeout, or cancellation.</summary>
    /// <param name="spec">What to run, with which arguments, input, environment, caps and timeout.</param>
    /// <param name="cancellationToken">Cancellation token; on cancellation the whole process tree is killed first.</param>
    /// <returns>
    /// The exit code and the captured output. A program that cannot start is a result with
    /// <see cref="ProcessResult.StartFailed"/> set, never an exception; a timeout kills the tree and sets
    /// <see cref="ProcessResult.TimedOut"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="spec"/> is null.</exception>
    /// <exception cref="OperationCanceledException">Thrown after the tree is killed when the caller cancels.</exception>
    Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken);
}
