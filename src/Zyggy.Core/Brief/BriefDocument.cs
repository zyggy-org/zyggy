namespace Zyggy.Core.Brief;

/// <summary>One Mail line of the brief, as the validator settled it.</summary>
internal sealed record MailLine(string Id, MailClass Class, string Time, string Received, string SenderName, string Subject, string Summary, string Decision);

/// <summary>One "Work in progress" line.</summary>
internal sealed record FileLine(string Name, string Drive, string Folder, string Time, string Modified, string By, string About, string? YouAction);

/// <summary>One "Only you can do these" line.</summary>
internal sealed record YouItem(string Action, string SenderName, string Subject, string Why, bool FromUrgent);

/// <summary>One "For the long run" line (spec 35 AC-35): the kept suggestion, its first basis and what Zyggy could prepare (null = "you").</summary>
internal sealed record IdeaLine(int N, string Id, string Area, string Text, string WhyNow, string? Prepare, string BasisFile, string BasisLine);

/// <summary>The brief's counts, printed in the header and kept in the sidecar.</summary>
internal sealed record BriefCounts(int Urgent, int Important, int Other, int Files, int FilesOther)
{
    public int NewMails => Urgent + Important + Other;
}

/// <summary>
/// The validated brief (spec 35 Step 5): one record rendered to both the <c>.md</c> and the sidecar, so the text and the item list can
/// never disagree. Subjects and names come from the pre-pass, never from model text.
/// </summary>
internal sealed record BriefDocument(
    DateOnly Date,
    DateTimeOffset Generated,
    BriefMode Mode,
    string Watermark,
    string Audit,
    IReadOnlyList<string> AuditReasons,
    IReadOnlyList<MailLine> Mail,
    IReadOnlyList<string> OtherMailIds,
    IReadOnlyList<FileLine> Files,
    int FilesOther,
    IReadOnlyList<ZItem> Items,
    IReadOnlyList<YouItem> You,
    IReadOnlyList<IdeaLine> Ideas,
    BriefCounts Counts)
{
    /// <summary>Gets a value indicating whether urgent content alone exceeded the page (set by the renderer).</summary>
    public bool PageExceeded { get; init; }

    /// <summary>Gets why the ideas run gave nothing today (AC-37), or <see langword="null"/>.</summary>
    public string? IdeasNote { get; init; }

    /// <summary>Gets a value indicating whether the ideas run is off (<c>ideas_cap</c> 0): the section is omitted.</summary>
    public bool IdeasOff { get; init; }

    /// <summary>Gets the one <see cref="ZKind.FileOther"/> item, when there are "other" mails.</summary>
    public ZItem? FileOtherItem => Items.FirstOrDefault(i => i.Kind == ZKind.FileOther);
}
