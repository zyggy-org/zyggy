using System.Text;
using System.Text.RegularExpressions;

using Zyggy.Core.Envelope;

namespace Zyggy.Core.Bus;

/// <summary>
/// The body of a job under a mandate (deliverable 18, "A job under a mandate has no free text"): one template per kind, embedded in
/// the binary as <c>Zyggy.Core.Bus.MandateJobs.&lt;kind&gt;.md</c>, filled only with the mandate's <c>spec</c> and <c>spec_commit</c>,
/// the step label and the reference id. Nothing a model, a report or the bus holds can reach the text otherwise.
/// </summary>
public static partial class MandateJobTemplates
{
    private const string Absent = "(none)";

    /// <summary>Returns the template of <paramref name="kind"/> as shipped, with LF line endings.</summary>
    /// <param name="kind">The kind of job.</param>
    /// <returns>The template text with its <c>{{spec}}</c>, <c>{{spec_commit}}</c>, <c>{{step}}</c> and <c>{{ref}}</c> placeholders.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the embedded template is missing (a build defect).</exception>
    public static string Template(MandateJobKind kind)
    {
        string name = "Zyggy.Core.Bus.MandateJobs." + EnvelopeWire.ToWire(kind) + ".md";
        using Stream stream = typeof(MandateJobTemplates).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("The embedded resource " + name + " is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    /// <summary>Renders the body of a job of <paramref name="kind"/>: the template with its placeholders filled, one final newline.</summary>
    /// <param name="kind">The kind of job.</param>
    /// <param name="spec">The mandate's <c>spec</c>, or <see langword="null"/> when it names none.</param>
    /// <param name="specCommit">The mandate's <c>spec_commit</c>, or <see langword="null"/>.</param>
    /// <param name="step">The step label.</param>
    /// <param name="reference">The reference id (<c>--ref</c>), or <see langword="null"/>.</param>
    /// <returns>The body text.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="step"/> is null.</exception>
    public static string Render(MandateJobKind kind, string? spec, string? specCommit, string step, EnvelopeId? reference)
    {
        ArgumentNullException.ThrowIfNull(step);
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["spec_commit"] = specCommit ?? Absent,
            ["spec"] = spec ?? Absent,
            ["step"] = step,
            ["ref"] = reference?.Value ?? Absent,
        };

        // One pass (AC-20): a value is inserted once and never searched for a placeholder again.
        return Placeholder().Replace(Template(kind), m => values[m.Groups[1].Value]).TrimEnd('\n') + "\n";
    }

    [GeneratedRegex(@"\{\{(spec_commit|spec|step|ref)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();
}
