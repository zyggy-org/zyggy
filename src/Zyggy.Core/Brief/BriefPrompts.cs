using System.Reflection;

namespace Zyggy.Core.Brief;

/// <summary>The brief's model contract embedded in <c>Zyggy.Core</c> (spec 35): the mail run's structured-output schema and, from Step 10, the ideas run's prompt and schema.</summary>
internal sealed class BriefPrompts
{
    public BriefPrompts()
    {
        MailSchema = Load("brief-mail.schema.json");
    }

    /// <summary>Gets the draft-07 schema of the mail run's structured output.</summary>
    public string MailSchema { get; }

    private static string Load(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Zyggy.Core.Brief.Prompts." + name)
            ?? throw new InvalidOperationException($"Embedded resource {name} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
