namespace Zyggy.Core.M365.Guard;

/// <summary>
/// The guard's answer. There is no "allow" and no "ask": <see cref="Pass"/> leaves the call to the template's <c>permissions.ask</c> rule
/// (the owner's prompt), <see cref="Deny"/> refuses it before the prompt, <see cref="Fail"/> is the guard's own failure (fail closed).
/// </summary>
internal abstract record GuardDecision
{
    /// <summary>Gets the decision that defers to the permission prompt (no output, exit 0).</summary>
    public static GuardDecision Pass { get; } = new PassDecision();

    private GuardDecision()
    {
    }

    /// <summary>The call is within policy; the permission prompt decides.</summary>
    public sealed record PassDecision : GuardDecision;

    /// <summary>The call is outside policy: the deny line with this reason.</summary>
    public sealed record Deny(string Reason) : GuardDecision;

    /// <summary>The guard could not decide (a Graph read failed): exit 2, Claude Code blocks the call.</summary>
    public sealed record Fail(string Message) : GuardDecision;
}
