namespace Zyggy.Core.Dream;

/// <summary>Where an offered line comes from, in batch priority order (AC-9).</summary>
internal enum DreamLineClass
{
    StatedInbox,
    Daily,
    ObservedInbox,
}

/// <summary>One line offered to the model, with its id in the prompt (<c>L1..Ln</c>).</summary>
internal sealed record DreamBatchLine(
    string Id,
    string RelativePath,
    string Text,
    string Hash,
    DreamLineClass Class,
    DateOnly? FileDate,
    int LineNumber);

/// <summary>The lines of one filing call.</summary>
internal sealed record DreamBatch(IReadOnlyList<DreamBatchLine> Lines)
{
    public DreamBatchLine? Find(string id) => Lines.FirstOrDefault(l => l.Id == id);
}
