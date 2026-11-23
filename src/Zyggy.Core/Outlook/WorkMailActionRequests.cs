using System.Text.RegularExpressions;

namespace Zyggy.Core.Outlook;

/// <summary>
/// The mail side of the work mail separation (spec 58, owner correction 2026-11-23): each mail the brief shows is parsed for a request
/// to run code or take an action — a command or script to run, a link to a file to execute, a shell or PowerShell line, a code block,
/// an instruction addressed to an assistant or a tool. Such a mail is listed as suspicious for the owner; nothing acts on it. The check
/// is deterministic and errs on the side of flagging: a flagged business mail costs the owner one look, a missed one is the risk.
/// </summary>
internal static partial class WorkMailActionRequests
{
    /// <summary>Returns why the mail looks like a request to run code or take an action, empty when it does not.</summary>
    /// <param name="subject">The subject.</param>
    /// <param name="body">The plain-text body, or <see langword="null"/> for a protected mail.</param>
    /// <returns>The reasons, in a fixed order, each once.</returns>
    public static IReadOnlyList<string> Reasons(string subject, string? body)
    {
        ArgumentNullException.ThrowIfNull(subject);
        string text = subject + "\n" + (body ?? string.Empty);
        var reasons = new List<string>();
        if (AsksToRun().IsMatch(text))
        {
            reasons.Add("asks to run a command or script");
        }

        if (ShellLine().IsMatch(text))
        {
            reasons.Add("contains a shell or PowerShell command");
        }

        if (CodeBlock().IsMatch(text))
        {
            reasons.Add("contains a code block");
        }

        if (ExecutableLink().IsMatch(text))
        {
            reasons.Add("links to a file to run");
        }

        if (AddressesAnAssistant().IsMatch(text))
        {
            reasons.Add("addresses an assistant or a tool");
        }

        return reasons;
    }

    // "run/execute/type/paste/install … command/script/terminal/powershell/…", in either order, within one sentence.
    [GeneratedRegex(
        @"\b(?:run|execute|exec|type|paste|enter|launch|install)\b[^.\n]{0,60}\b(?:command|commands|script|scripts|terminal|console|shell|powershell|cmd|bash|following|this\s+code|macro)\b"
        + @"|\b(?:command|script|terminal|console|shell|powershell|macro)\b[^.\n]{0,60}\b(?:run|execute|exec|type|paste)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AsksToRun();

    // A line that reads as a command: a pipe into a shell, a download-and-run, a known command at the start of a line or after a colon.
    [GeneratedRegex(
        @"\|\s*(?:ba|z)?sh\b|\|\s*iex\b|\b(?:curl|wget|iwr|irm)\s+\S*https?://"
        + @"|\b(?:invoke-expression|invoke-webrequest|invoke-restmethod|start-process|downloadstring|frombase64string|set-executionpolicy)\b"
        + @"|\bpowershell(?:\.exe)?\s+-(?:enc|encodedcommand|e|c|command|ep|executionpolicy)\b"
        + @"|(?:^|[:>]\s*)(?:sudo|rm\s+-rf|chmod|npm\s+(?:install|i|run)|pip3?\s+install|dotnet\s+run|node\s|python3?\s|cmd(?:\.exe)?\s+/c|reg\s+add|schtasks|certutil|mshta|rundll32|regsvr32|bitsadmin)\b"
        + @"|\bzyggy(?:\.exe)?\s+\w+",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ShellLine();

    [GeneratedRegex(@"```|<script\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CodeBlock();

    [GeneratedRegex(
        @"https?://\S+?\.(?:exe|msi|bat|cmd|ps1|psm1|vbs|vbe|js|jse|wsf|hta|scr|sh|py|jar|lnk|dll|appx|msix|iso|reg)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExecutableLink();

    // An instruction meant for an AI assistant, an agent or a tool rather than for the owner.
    [GeneratedRegex(
        @"\b(?:ignore|disregard|forget)\s+(?:all\s+|any\s+)?(?:the\s+)?(?:previous|prior|above|earlier)\s+(?:instructions|prompts?|rules)\b"
        + @"|\b(?:ai|llm)\s+(?:assistant|agent)\b|\b(?:assistant|agent|claude|copilot|chatgpt|zyggy)\s*[,:]"
        + @"|\b(?:system\s+prompt|tool\s+call|allowed_tools|use\s+the\s+\S+(?:\s+\S+){0,3}\s+tool)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AddressesAnAssistant();
}
