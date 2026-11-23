using Zyggy.Core.Models;

namespace Zyggy.Core.Supervision;

/// <summary>
/// The supervisor run's model (spec 50, "Supervisor run", Q7): <c>instance/models.json</c> <c>supervisor</c> in Central's own project
/// (<c>supervision.project</c>, the project the run already runs in). No file or <c>null</c> → today's run; an invalid table → no model
/// and the error, and the wake starts no run. Never a fallback.
/// </summary>
/// <param name="Model">The model id the run must run on, or <see langword="null"/> for today's default.</param>
/// <param name="Error">The table's error line, when the file is invalid.</param>
internal sealed record SupervisorModel(string? Model, string? Error)
{
    /// <summary>Reads the table of the project at <paramref name="projectDirectory"/>.</summary>
    /// <param name="projectDirectory">Central's own project folder.</param>
    /// <param name="readFile">Reads a file; <see langword="null"/> when it does not exist.</param>
    /// <returns>The model or the error.</returns>
    public static SupervisorModel For(string projectDirectory, Func<string, string?> readFile)
    {
        ArgumentNullException.ThrowIfNull(projectDirectory);
        ModelTableResult result = ModelTable.Load(Path.Join(projectDirectory, "instance", ModelTable.FileName), readFile);
        return result.Error is { } error ? new SupervisorModel(null, error) : new SupervisorModel(result.Table?.Supervisor, null);
    }
}
