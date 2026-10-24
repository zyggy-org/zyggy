namespace Zyggy.Core.Models;

/// <summary>
/// Runs one headless model session (founding spec §9 seam; the v1 implementation drives the Claude Code CLI).
/// Nothing outside the implementation references <c>claude</c>.
/// </summary>
public interface IModelRunner
{
    /// <summary>Runs <paramref name="request"/> and reports its outcome.</summary>
    /// <param name="request">The prompt, working directory, limits and session isolation.</param>
    /// <param name="cancellationToken">Cancellation token; on cancellation the model process tree is killed first.</param>
    /// <returns>
    /// A <see cref="ModelRunResult"/>; every failure is a result with a closed reason and a detail token, never an exception.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is null.</exception>
    /// <exception cref="OperationCanceledException">Thrown after the process tree is killed when the caller cancels.</exception>
    Task<ModelRunResult> RunAsync(ModelRunRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Whether the model CLI is logged in (founding spec §11 <c>claudeAuthOk</c> as amended by A7-4; spec 07 ex-Q3): the v1
    /// implementation runs <c>claude auth status</c>. <see langword="true"/> logged in, <see langword="false"/> not logged in,
    /// <see langword="null"/> when it could not tell (executable missing, timeout, an unexpected exit code).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token; the only reason this method throws.</param>
    /// <returns>The verdict, or <see langword="null"/>.</returns>
    /// <exception cref="OperationCanceledException">Thrown when the caller cancels.</exception>
    Task<bool?> CheckAuthAsync(CancellationToken cancellationToken);
}
