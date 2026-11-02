using System.Text;

using Zyggy.Core.Envelope;
using Zyggy.Core.Memory;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Bus;

/// <summary>
/// The body of Central's report on an item (spec 41 Contracts "Central's report", plan 41 A9), every line ended by <c>\n</c>; data for
/// the sender, never instructions.
/// </summary>
internal static class PickupItemReport
{
    /// <summary>
    /// The <c>done</c> body: <c>archived:</c> and <c>sidecar:</c> lines, the index line, <c>commit: &lt;sha&gt; pushed | push deferred</c>;
    /// for an item already filed, <c>already filed</c> in place of the index line (and the commit line only when its paths were committed
    /// now); for the OneDrive route, <c>onedrive: &lt;path&gt; (adopted by the next offload)</c>.
    /// </summary>
    public static byte[] DoneBody(ArchiveOutcome outcome, string? oneDrivePath)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var body = new StringBuilder();
        body.Append("archived: ").Append(outcome.Item).Append('\n');
        body.Append("sidecar: ").Append(outcome.Sidecar).Append('\n');
        body.Append(outcome.Kind == ArchiveOutcomeKind.AlreadyFiled ? "already filed" : outcome.Line).Append('\n');
        if (outcome.Sha is not null)
        {
            body.Append("commit: ").Append(outcome.Sha).Append(outcome.Pushed ? " pushed" : " push deferred").Append('\n');
        }

        if (oneDrivePath is not null)
        {
            body.Append("onedrive: ").Append(oneDrivePath).Append(" (adopted by the next offload)\n");
        }

        return Encoding.UTF8.GetBytes(body.ToString());
    }

    /// <summary>
    /// Whether <paramref name="report"/> is Central's report on an item (owner 2026-11-02, so <c>zyggy status --id</c> still says
    /// <c>item</c> once the <c>items/</c> file is gone): from <c>central</c>, and either <c>failed</c> with <c>reason: archive_refused</c>
    /// (a reason no job report carries) or <c>done</c> with the body this class writes (<c>archived:</c> then <c>sidecar:</c>).
    /// </summary>
    /// <param name="report">A verified report answering the id.</param>
    /// <param name="body">That report's body.</param>
    public static bool IsItemReport(EnvelopeSummary report, string? body)
    {
        ArgumentNullException.ThrowIfNull(report);
        return report.Verified && report.From == MachineName.Central && report.Status switch
        {
            ReportStatus.Failed => report.Reason == BusPickup.ArchiveRefusedReason,
            ReportStatus.Done => body is not null && body.StartsWith("archived: ", StringComparison.Ordinal) && body.Contains("\nsidecar: ", StringComparison.Ordinal),
            _ => false,
        };
    }

    /// <summary>
    /// The <c>failed</c> body: <c>archive: refused: &lt;word&gt; (&lt;detail&gt;)</c> rendered as <c>archive add</c> renders it; for the
    /// OneDrive route a second line saying the copy stays.
    /// </summary>
    public static byte[] RefusedBody(string word, string? detail, string? oneDrivePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(word);
        string body = "archive: refused: " + ArchiveVerb.Render(word, detail) + "\n"
            + (oneDrivePath is null ? string.Empty : $"the OneDrive copy at {oneDrivePath} stays; delete it in OneDrive if wanted\n");
        return Encoding.UTF8.GetBytes(body);
    }
}
