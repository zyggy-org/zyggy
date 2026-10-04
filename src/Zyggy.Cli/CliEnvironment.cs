using System.Collections;

namespace Zyggy.Cli;

/// <summary>
/// The process environment as the verbs see it, injected so nothing reads <see cref="Environment"/> statically.
/// An empty value counts as unset, as in the 27 shell hooks (<c>${VAR:-}</c>).
/// </summary>
internal sealed class CliEnvironment(IReadOnlyDictionary<string, string?> variables)
{
    public static CliEnvironment FromProcess()
    {
        var variables = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            variables[(string)entry.Key] = entry.Value as string;
        }

        return new CliEnvironment(variables);
    }

    public string? Get(string name) => variables.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value) ? value : null;

    public bool HooksOff => Get("ZYGGY_HOOKS") == "off";
}
