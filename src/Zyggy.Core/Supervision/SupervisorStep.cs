using System.Text;
using System.Text.RegularExpressions;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Supervision;

/// <summary>What wakes a supervisor run (deliverable 18, "The supervisor run").</summary>
internal enum SupervisorEventKind
{
    /// <summary>A report answering a job that carries a mandate.</summary>
    Report,

    /// <summary>A question an agent addressed to Central.</summary>
    Question,
}

/// <summary>
/// One event on Central's checkout: ids and names only, never a body or any field an agent wrote as text, so nothing an agent wrote
/// can reach the supervisor's prompt (§8).
/// </summary>
/// <param name="Id">The report's or question's id.</param>
/// <param name="Kind">Report or question.</param>
/// <param name="Machine">The machine that wrote it.</param>
/// <param name="Mandate">The mandate a report carries; <see langword="null"/> for a question.</param>
/// <param name="MandateState">That mandate's state when the events were read; <see langword="null"/> for a question.</param>
/// <param name="Handled"><see langword="true"/> once a decision of Central has it as <c>in_reply_to</c>.</param>
internal sealed record SupervisorEvent(EnvelopeId Id, SupervisorEventKind Kind, MachineName Machine, EnvelopeId? Mandate, MandateState? MandateState, bool Handled);

/// <summary>
/// The pure part of one supervisor step (deliverable 18, slice 9): which event is next, and the fixed prompt that names it. The
/// prompt is the embedded template <c>Zyggy.Core.Supervision.Prompts.supervisor-step.md</c> filled in one pass with the event's id,
/// kind, machine and mandate id — values the bus validated as ids and names, never text.
/// </summary>
internal static partial class SupervisorStep
{
    private const string ResourceName = "Zyggy.Core.Supervision.Prompts.supervisor-step.md";

    private const string NoMandate = "none";

    /// <summary>
    /// The oldest event (id order) that no decision replies to yet and that may wake a run: every question, and a report only while
    /// its mandate is <see cref="MandateState.Active"/>.
    /// </summary>
    public static SupervisorEvent? Next(IReadOnlyList<SupervisorEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        return events
            .Where(e => !e.Handled && (e.Kind == SupervisorEventKind.Question || e.MandateState == MandateState.Active))
            .OrderBy(e => e.Id.Value, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>The prompt template as shipped, with LF line endings.</summary>
    public static string Template()
    {
        using Stream stream = typeof(SupervisorStep).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The embedded resource " + ResourceName + " is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    /// <summary>The prompt of one step on <paramref name="supervisorEvent"/>.</summary>
    public static string Prompt(SupervisorEvent supervisorEvent)
    {
        ArgumentNullException.ThrowIfNull(supervisorEvent);
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["event"] = supervisorEvent.Id.Value,
            ["kind"] = supervisorEvent.Kind == SupervisorEventKind.Report ? "report" : "question",
            ["machine"] = supervisorEvent.Machine.Value,
            ["mandate"] = supervisorEvent.Mandate?.Value ?? NoMandate,
        };

        // One pass: a value is never searched for a placeholder again.
        return Placeholder().Replace(Template(), m => values[m.Groups[1].Value]);
    }

    [GeneratedRegex(@"\{\{(event|kind|machine|mandate)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();
}
