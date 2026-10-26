namespace Zyggy.Core.Secrets.Verbs;

/// <summary>One line read from a terminal without echo (spec 09 AC-7); the CLI implements it over the console, tests script it.</summary>
public interface IHiddenInput
{
    /// <summary>Reads one line without echo; <see langword="null"/> when the input ended or the owner aborted (Ctrl+C / Ctrl+D).</summary>
    string? ReadLine(CancellationToken cancellationToken);
}
