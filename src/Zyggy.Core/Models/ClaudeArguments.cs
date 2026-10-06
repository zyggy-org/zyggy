using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Zyggy.Core.Models;

/// <summary>
/// Builds the Claude Code argument vector from a <see cref="ModelRunRequest"/>, literally the spec's Invocation contract.
/// No request field can produce a permission-bypassing, bare, resume or continue flag; the prompt never becomes an argument.
/// </summary>
internal static class ClaudeArguments
{
    public static IReadOnlyList<string> Build(ModelRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        List<string> args =
        [
            "-p",
            "--output-format", "stream-json", "--verbose",
            "--permission-mode", "auto",
            "--permission-prompts", "none",
            "--no-session-persistence",
            "--max-turns", request.MaxTurns.ToString(CultureInfo.InvariantCulture),
        ];

        if (request.MaxBudgetUsd is { } budget)
        {
            args.Add("--max-budget-usd");
            args.Add(budget.ToString(CultureInfo.InvariantCulture));
        }

        if (request.Tools is { } tools)
        {
            args.Add("--tools");
            args.Add(JoinValues(tools, nameof(request.Tools)));
        }

        if (request.AllowedTools.Count > 0)
        {
            args.Add("--allowedTools");
            args.Add(JoinValues(request.AllowedTools, nameof(request.AllowedTools)));
        }

        var noMcp = request.Isolation.HasFlag(ModelSessionIsolation.NoMcp);
        if (request.DisallowedTools.Count > 0)
        {
            // One list: with NoMcp, mcp__* leads it (never two --disallowedTools flags).
            args.Add("--disallowedTools");
            args.Add((noMcp ? "mcp__*," : string.Empty) + JoinValues(request.DisallowedTools, nameof(request.DisallowedTools)));
        }

        foreach (var directory in request.AdditionalDirectories)
        {
            args.Add("--add-dir");
            args.Add(Value(directory, nameof(request.AdditionalDirectories)));
        }

        if (request.JsonSchema is { } schema)
        {
            args.Add("--json-schema");
            args.Add(schema);
        }

        if (request.Model is { } model)
        {
            args.Add("--model");
            args.Add(Value(model, nameof(request.Model)));
        }

        if (request.AppendSystemPrompt is { } system)
        {
            args.Add("--append-system-prompt");
            args.Add(system);
        }

        if (noMcp)
        {
            args.AddRange(request.DisallowedTools.Count > 0 ? ["--strict-mcp-config"] : ["--strict-mcp-config", "--disallowedTools", "mcp__*"]);
        }

        if (Settings(request.Isolation) is { } settings)
        {
            args.Add("--settings");
            args.Add(settings);
        }

        if (request.Isolation.HasFlag(ModelSessionIsolation.NoSlashCommands))
        {
            args.Add("--disable-slash-commands");
        }

        if (request.McpConfig is { } mcpConfig)
        {
            if (noMcp)
            {
                throw new ArgumentException("An MCP configuration contradicts NoMcp.", nameof(request));
            }

            args.AddRange(["--strict-mcp-config", "--mcp-config", Value(mcpConfig, nameof(request.McpConfig))]);
        }

        return args;
    }

    private static string? Settings(ModelSessionIsolation isolation)
    {
        var noHooks = isolation.HasFlag(ModelSessionIsolation.NoHooks);
        var noAutoMemory = isolation.HasFlag(ModelSessionIsolation.NoAutoMemory);
        if (!noHooks && !noAutoMemory)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            if (noHooks)
            {
                writer.WriteBoolean("disableAllHooks", true);
            }

            if (noAutoMemory)
            {
                writer.WriteBoolean("autoMemoryEnabled", false);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string JoinValues(IReadOnlyList<string> values, string field) =>
        string.Join(',', values.Select(v => Value(v, field)));

    // A value that starts with a dash could be read as a flag by the CLI; refuse it rather than smuggle one in.
    private static string Value(string value, string field) =>
        value.StartsWith('-')
            ? throw new ArgumentException($"A {field} value must not start with '-'.", field)
            : value;
}
