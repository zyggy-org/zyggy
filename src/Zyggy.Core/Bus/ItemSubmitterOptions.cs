namespace Zyggy.Core.Bus;

/// <summary>
/// What <see cref="ItemSubmitter"/> needs from the verb beyond <c>node.json</c> to build the laptop deny list (spec 41 AC-12): the
/// process environment and the path of the <c>node.json</c> in use. Registered by <c>zyggy archive</c>.
/// </summary>
public sealed class ItemSubmitterOptions
{
    /// <summary>Gets the process environment (<c>HOME</c>, <c>XDG_CONFIG_HOME</c>, <c>CREDENTIALS_DIRECTORY</c> …).</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();

    /// <summary>Gets the path of the <c>node.json</c> in use; its folder is denied as a source.</summary>
    public required string NodeJsonPath { get; init; }
}
