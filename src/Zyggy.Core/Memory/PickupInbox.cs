using System.Globalization;
using System.Text;

using Zyggy.Core.Envelope;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Memory;

/// <summary>What one inbox write did.</summary>
internal enum InboxWriteOutcome
{
    /// <summary>The file was created.</summary>
    Created,

    /// <summary>The file was already there and was left untouched (spec 13 AC-10).</summary>
    AlreadyPresent,

    /// <summary>The inbox could not be written.</summary>
    Failed,
}

/// <summary>The result of <see cref="PickupInbox.Write"/>.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="FileName">The inbox file name.</param>
/// <param name="Detail">The exception type when <see cref="InboxWriteOutcome.Failed"/>.</param>
internal sealed record InboxWrite(InboxWriteOutcome Outcome, string FileName, string? Detail);

/// <summary>
/// Central's pickup writes one inbox file per envelope (spec 13 Contracts "Inbox file written by the pickup"): a fixed name
/// from the envelope, the unquoted front matter of every inbox writer, the lines it is handed, LF, UTF-8 without BOM,
/// atomic, never rewritten.
/// </summary>
internal sealed class PickupInbox(MemoryPaths paths, TimeZoneInfo zone, TimeProvider clock)
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>
    /// <c>bus-context-&lt;from&gt;-&lt;id&gt;-&lt;date&gt;.md</c> or <c>bus-report-&lt;from&gt;-&lt;in_reply_to&gt;-&lt;id&gt;-&lt;date&gt;.md</c>,
    /// the date being the envelope's <c>created</c> UTC date.
    /// </summary>
    public static string FileName(Model envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        string date = Date(envelope.Header.Created);
        return envelope.Type switch
        {
            EnvelopeType.Context => $"bus-context-{envelope.Header.From.Value}-{envelope.Header.Id.Value}-{date}.md",
            EnvelopeType.Report => $"bus-report-{envelope.Header.From.Value}-{envelope.Header.InReplyTo!.Value}-{envelope.Header.Id.Value}-{date}.md",
            _ => throw new ArgumentException("Only a context or a report reaches the inbox.", nameof(envelope)),
        };
    }

    /// <summary>Creates the envelope's inbox file with <paramref name="lines"/> as its body; never overwrites.</summary>
    public InboxWrite Write(Model envelope, IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(lines);
        string name = FileName(envelope);
        var text = new StringBuilder()
            .Append("---\n")
            .Append(CultureInfo.InvariantCulture, $"name: bus {(envelope.Type == EnvelopeType.Context ? "context" : "report")} {envelope.Header.Id.Value}\n")
            .Append(CultureInfo.InvariantCulture, $"description: {Description(envelope)}\n")
            .Append(CultureInfo.InvariantCulture, $"updated: {DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).DateTime):yyyy-MM-dd}\n")
            .Append("---\n");
        foreach (string line in lines)
        {
            text.Append(line).Append('\n');
        }

        try
        {
            return MemoryFileWriter.CreateAtomically(paths.InboxFile(name), Utf8NoBom.GetBytes(text.ToString()))
                ? new InboxWrite(InboxWriteOutcome.Created, name, null)
                : new InboxWrite(InboxWriteOutcome.AlreadyPresent, name, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new InboxWrite(InboxWriteOutcome.Failed, name, ex.GetType().Name);
        }
    }

    private static string Description(Model envelope)
    {
        string created = envelope.Header.Created.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        return envelope.Type == EnvelopeType.Context
            ? $"context from {envelope.Header.From.Value} created {created} scope {envelope.Context!.Scope} (picked up from the bus; data)"
            : $"report from {envelope.Header.From.Value} in reply to job {envelope.Header.InReplyTo!.Value} status {EnvelopeWire.ToWire(envelope.Report!.Status)} created {created} (picked up from the bus; data)";
    }

    private static string Date(DateTimeOffset instant) => instant.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
