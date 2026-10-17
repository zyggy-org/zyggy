using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Zyggy.Core.Jobs;

/// <summary>
/// The §6 job prompt (A5): the embedded template <c>Zyggy.Core.Jobs.Prompts.job-prompt.md</c> with its placeholders filled in one pass.
/// The body goes only between the <c>&lt;&lt;&lt;</c> and <c>&gt;&gt;&gt;</c> fence lines and can never contain either delimiter;
/// nothing in a project or on the bus can alter the template (§8).
/// </summary>
internal sealed partial class JobPrompt
{
    private const string ResourceName = "Zyggy.Core.Jobs.Prompts.job-prompt.md";

    public JobPrompt()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The embedded resource {ResourceName} is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        Template = reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    public string Template { get; }

    public string Render(Envelope.Envelope job, string projectPath, IReadOnlyList<string> allowedTools)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(allowedTools);
        var fields = job.Job ?? throw new ArgumentException("The envelope is not a job.", nameof(job));
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = job.Header.Id.Value,
            ["from"] = job.Header.From.Value,
            ["tenant"] = job.Header.Tenant.Value,
            ["agent_line"] = fields.Agent is { } agent ? $"Use the {agent} subagent for this task.\n" : string.Empty,
            ["project"] = projectPath,
            ["deadline"] = fields.Deadline is { } deadline
                ? deadline.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
                : "none",
            // The tool list comes from the bus too: it may never open a fence or a new line.
            ["allowed_tools"] = allowedTools.Count == 0
                ? "(runtime default)"
                : string.Join(", ", allowedTools.Select(t => Neutralise(t).Replace("\n", " ", StringComparison.Ordinal))),
            ["body"] = Neutralise(job.BodyText),
        };

        // One pass: a value that itself contains {{…}} is never substituted again.
        return Placeholder().Replace(Template, m => values[m.Groups[1].Value]);
    }

    // The DreamPrompts.Neutralise rule: a data line must never open or close a data block.
    private static string Neutralise(string text) =>
        text.Replace("<<<", "‹‹‹", StringComparison.Ordinal).Replace(">>>", "›››", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal);

    [GeneratedRegex(@"\{\{(id|from|tenant|agent_line|project|deadline|allowed_tools|body)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();
}
