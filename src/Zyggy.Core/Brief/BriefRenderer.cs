using System.Globalization;
using System.Text;

namespace Zyggy.Core.Brief;

/// <summary>
/// Renders a <see cref="BriefDocument"/> in the spec's "Rendered brief" format (R2 as revised by OD-6), then applies the page cap; the
/// result is the complete form with page markers, which <c>show</c> prints as the page and <c>show --full</c> whole.
/// </summary>
internal static class BriefRenderer
{
    public const string ICanDoTitle = "## I can do these — say \"do Z1, Z3\" or \"do all Z\" (each one asks you to confirm)";

    public static (string Markdown, bool PageExceeded) RenderMarkdown(BriefDocument document, int maxLines, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(document);
        var lines = Lines(document);
        var (capped, exceeded) = PageCap.Apply(lines, maxLines, maxChars);
        var sb = new StringBuilder();
        foreach (var line in capped)
        {
            sb.Append(line).Append('\n');
        }

        return (sb.ToString(), exceeded);
    }

    /// <summary>The lines before the page cap, each with its kind.</summary>
    public static IReadOnlyList<RenderedLine> Lines(BriefDocument d)
    {
        ArgumentNullException.ThrowIfNull(d);
        var lines = new List<RenderedLine>();
        void Fixed(string text) => lines.Add(new RenderedLine(text, LineKind.Fixed));
        if (d.Audit != "ok")
        {
            Fixed($"audit FLAGGED: {string.Join("; ", d.AuditReasons)}");
        }

        var date = BriefPaths.Iso(d.Date);
        if (d.Mode == BriefMode.Weekend)
        {
            Fixed($"Zyggy — morning brief {date} (weekend)");
            Fixed(string.Empty);
            Fixed("## For the long run");
            Ideas(d, lines);
            return lines;
        }

        var c = d.Counts;
        Fixed($"Zyggy — morning brief {date} ({c.NewMails} new mail{(c.NewMails == 1 ? string.Empty : "s")} since {d.Watermark}: {c.Urgent} urgent, {c.Important} important, {c.Other} other; {c.Files + c.FilesOther} changed file{(c.Files + c.FilesOther == 1 ? string.Empty : "s")})");
        Fixed(string.Empty);
        Fixed("## Mail");
        var order = 0;
        foreach (var m in d.Mail.Where(m => m.Class != MailClass.Other))
        {
            order++;
            var mark = m.Class == MailClass.Urgent ? "! " : string.Empty;
            lines.Add(new RenderedLine($"- {mark}{m.Time} {m.SenderName} — {m.Subject} — {m.Summary} → {m.Decision}", m.Class == MailClass.Urgent ? LineKind.MailUrgent : LineKind.MailImportant, m.Class == MailClass.Urgent, order));
        }

        if (d.OtherMailIds.Count > 0 && d.FileOtherItem is { } fileOther)
        {
            Fixed($"- {d.OtherMailIds.Count} other mail{(d.OtherMailIds.Count == 1 ? string.Empty : "s")}, none needing you → Z{fileOther.N}");
        }

        if (d.Mail.Count == 0)
        {
            Fixed("- none");
        }

        Fixed(string.Empty);
        Fixed("## Work in progress");
        order = 0;
        foreach (var f in d.Files)
        {
            order++;
            var action = f.YouAction is { } ya ? $"you: {ya}" : "nothing";
            lines.Add(new RenderedLine($"- {f.Name} ({f.Drive}:{f.Folder}, modified {f.Time} by {f.By}) — {f.About} → {action}", LineKind.File, false, order));
        }

        if (d.FilesOther > 0)
        {
            Fixed($"- {d.FilesOther} other changed file{(d.FilesOther == 1 ? string.Empty : "s")}");
        }
        else if (d.Files.Count == 0)
        {
            Fixed("- none");
        }

        Fixed(string.Empty);
        Fixed(ICanDoTitle);
        foreach (var z in d.Items)
        {
            var text = z.Kind switch
            {
                ZKind.Send => $"Z{z.N}. send reply to {z.Sender.Name} \"RE: {z.Subject}\" (draft in Drafts) — {z.Why}",
                ZKind.Move => $"Z{z.N}. {(z.Destination == ZDestination.Archive ? "file" : "move")} \"{z.Subject}\" from {z.Sender.Name} → {Destination(z.Destination)} — {z.Why}",
                ZKind.DiscardDraft => $"Z{z.N}. discard the reply draft \"{z.Subject}\" → Deleted Items — {z.Why}",
                _ => $"Z{z.N}. file {z.MessageIds?.Count ?? 0} other mails → Archive",
            };
            lines.Add(new RenderedLine(text, z.Kind == ZKind.FileOther ? LineKind.Fixed : LineKind.ZItem, z.FromUrgent, z.N));
        }

        if (d.Items.Count == 0)
        {
            Fixed("- none");
        }

        Fixed(string.Empty);
        Fixed("## Only you can do these");
        order = 0;
        foreach (var y in d.You)
        {
            order++;
            lines.Add(new RenderedLine($"- {y.Action} — {y.SenderName} \"{y.Subject}\" — {y.Why}", LineKind.YouItem, y.FromUrgent, order));
        }

        if (d.You.Count == 0)
        {
            Fixed("- none");
        }

        Fixed(string.Empty);
        Fixed("## For the long run");
        Ideas(d, lines);
        return lines;
    }

    private static void Ideas(BriefDocument d, List<RenderedLine> lines)
    {
        foreach (var idea in d.Ideas)
        {
            lines.Add(new RenderedLine($"{idea.N}. {idea.Text}", LineKind.Idea, true, idea.N));
        }

        if (d.Ideas.Count == 0)
        {
            lines.Add(new RenderedLine("- none", LineKind.Fixed));
        }
    }

    private static string Destination(ZDestination? destination) => destination == ZDestination.Archive ? "Archive" : "Deleted Items";

    internal static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}
