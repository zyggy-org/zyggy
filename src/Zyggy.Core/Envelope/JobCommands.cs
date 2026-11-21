namespace Zyggy.Core.Envelope;

/// <summary>The shape of a <c>command</c> name (spec 59 slice 2): one or more of <c>a-z</c>, <c>0-9</c> and <c>-</c>, at most 64 characters.</summary>
internal static class JobCommands
{
    private const int MaxLength = 64;

    /// <summary>Whether <paramref name="name"/> is a command name a job or <c>node.json</c> may carry.</summary>
    public static bool IsName(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= MaxLength && name.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');
}
