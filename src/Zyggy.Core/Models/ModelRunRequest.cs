using System.Collections.ObjectModel;

namespace Zyggy.Core.Models;

/// <summary>
/// One headless model session: the prompt (sent on standard input, never as an argument), where it runs and what it may do.
/// Built for the dream pass and shaped so the job runner (06) reuses it unchanged.
/// </summary>
/// <param name="Prompt">The prompt text, written to standard input.</param>
/// <param name="WorkingDirectory">The process working directory (the CLI has no <c>--cwd</c>).</param>
/// <param name="Timeout">How long the session may run before its process tree is killed; must be positive.</param>
public sealed record ModelRunRequest(string Prompt, string WorkingDirectory, TimeSpan Timeout)
{
    /// <summary>Fixed instructions appended to the system prompt (an argument; never data).</summary>
    public string? AppendSystemPrompt { get; init; }

    /// <summary>The built-in tool set (<c>--tools</c>); <see langword="null"/> keeps the runtime default, empty allows none.</summary>
    public IReadOnlyList<string>? Tools { get; init; }

    /// <summary>Tool approval rules (<c>--allowedTools</c>).</summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = [];

    /// <summary>Gets the tools denied by name (<c>--disallowedTools</c>, one flag); empty for none — the 28 argument list unchanged.</summary>
    public IReadOnlyList<string> DisallowedTools { get; init; } = [];

    /// <summary>Extra directories the session may read (<c>--add-dir</c>).</summary>
    public IReadOnlyList<string> AdditionalDirectories { get; init; } = [];

    /// <summary>Maximum agentic turns (<c>--max-turns</c>).</summary>
    public int MaxTurns { get; init; } = 60;

    /// <summary>Per-session spending cap in USD (<c>--max-budget-usd</c>); <see langword="null"/> sets none.</summary>
    public decimal? MaxBudgetUsd { get; init; }

    /// <summary>A JSON Schema (draft-07) the result must satisfy (<c>--json-schema</c>); the result then requires structured output.</summary>
    public string? JsonSchema { get; init; }

    /// <summary>The model alias or name (<c>--model</c>); <see langword="null"/> keeps the default.</summary>
    public string? Model { get; init; }

    /// <summary>What the session is cut off from (MCP servers, hooks, auto memory, slash commands).</summary>
    public ModelSessionIsolation Isolation { get; init; } = ModelSessionIsolation.None;

    /// <summary>
    /// An MCP configuration file loaded with <c>--strict-mcp-config --mcp-config</c> instead of the project's own servers (the m365 runs:
    /// a project-scope headersHelper does not get <c>CREDENTIALS_DIRECTORY</c>); <see langword="null"/> leaves the argument list as before.
    /// </summary>
    public string? McpConfig { get; init; }

    /// <summary>Environment additions for the model process, for example <c>ZYGGY_HOOKS=off</c>.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = ReadOnlyDictionary<string, string>.Empty;

    /// <summary>Where a transcript is kept; <see langword="null"/> keeps none (the dream). Reserved for the job runner.</summary>
    public string? TranscriptPath { get; init; }

    /// <summary>Capture cap for the model's standard output in bytes; more is a failure (<c>output_too_large</c>).</summary>
    public int MaxCaptureBytes { get; init; } = 2 * 1024 * 1024;
}
