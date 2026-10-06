using System.Reflection;

namespace Zyggy.Core.Brief;

/// <summary>The brief's model contract embedded in <c>Zyggy.Core</c> (spec 35): the mail run's structured-output schema and the ideas run's prompt and schema.</summary>
internal sealed class BriefPrompts
{
    public BriefPrompts()
    {
        MailSchema = Load("brief-mail.schema.json");
        IdeasPrompt = Load("ideas.prompt.md");
        IdeasSchema = Load("ideas.schema.json");
    }

    /// <summary>Gets the draft-07 schema of the mail run's structured output.</summary>
    public string MailSchema { get; }

    /// <summary>Gets the ideas run's instructions (appended to the system prompt).</summary>
    public string IdeasPrompt { get; }

    /// <summary>Gets the draft-07 schema of the ideas run's structured output.</summary>
    public string IdeasSchema { get; }

    private static string Load(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Zyggy.Core.Brief.Prompts." + name)
            ?? throw new InvalidOperationException($"Embedded resource {name} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
