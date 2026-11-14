namespace Zyggy.Core.Jobs.Hook;

/// <summary>
/// The job hook's answer for one tool call (spec 15): allowed, or denied under one of four rules with a one-line detail.
/// </summary>
/// <param name="Allowed">Whether the call may run.</param>
/// <param name="Rule">Empty when allowed; else <see cref="NotAllowedRule"/>, <see cref="WriteOutsideRule"/>, <see cref="NetworkRule"/> or <see cref="HookErrorRule"/>.</param>
/// <param name="Detail">The command or path the decision is about, one line, at most <see cref="MaxDetail"/> characters.</param>
internal sealed record HookDecision(bool Allowed, string Rule, string Detail)
{
    public const string NotAllowedRule = "not in allowed_tools";

    public const string WriteOutsideRule = "write outside the project";

    public const string NetworkRule = "network program";

    public const string HookErrorRule = "hook error";

    public const int MaxDetail = 120;

    public static HookDecision Allow { get; } = new(true, string.Empty, string.Empty);

    public static HookDecision Deny(string rule, string detail) => new(false, rule, OneLine(detail));

    /// <summary>Folds line breaks and tabs to spaces and cuts the text at <see cref="MaxDetail"/> characters.</summary>
    public static string OneLine(string text)
    {
        var folded = text.Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('\t', ' ');
        return folded.Length <= MaxDetail ? folded : folded[..MaxDetail];
    }
}
