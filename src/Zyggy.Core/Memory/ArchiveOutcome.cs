namespace Zyggy.Core.Memory;

/// <summary>How an <c>archive add</c> ended.</summary>
internal enum ArchiveOutcomeKind
{
    /// <summary>Item, sidecar and index line written and committed; <see cref="ArchiveOutcome.Pushed"/> says whether the push went through.</summary>
    Archived,

    /// <summary>A closed check refused the item (exit 2).</summary>
    Refused,

    /// <summary>Git refused before or after the write (exit 6); <see cref="ArchiveOutcome.Detail"/> names the step.</summary>
    GitError,
}

/// <summary>The outcome of <see cref="ArchiveService.AddAsync"/>: what was written, where, and the commit — or why it stopped.</summary>
/// <param name="Kind">The kind.</param>
/// <param name="Refusal">The refusal when <see cref="ArchiveOutcomeKind.Refused"/>.</param>
/// <param name="Detail">The refusal's detail token, or the failed git step.</param>
/// <param name="Item">The item's path relative to the principal directory.</param>
/// <param name="Sidecar">The sidecar's path relative to the principal directory.</param>
/// <param name="InboxPath">The full path of the inbox file the index line went to.</param>
/// <param name="Line">The index line.</param>
/// <param name="Sha">The commit's hash.</param>
/// <param name="Pushed">Whether the commit was pushed.</param>
/// <param name="Note">A note for stderr (no memory file named after the project yet).</param>
internal sealed record ArchiveOutcome(
    ArchiveOutcomeKind Kind,
    ArchiveRefusal? Refusal,
    string? Detail,
    string? Item,
    string? Sidecar,
    string? InboxPath,
    string? Line,
    string? Sha,
    bool Pushed,
    string? Note)
{
    public static ArchiveOutcome Refused(ArchiveRefusal refusal, string? detail) => new(ArchiveOutcomeKind.Refused, refusal, detail, null, null, null, null, null, false, null);

    public static ArchiveOutcome GitError(string step) => new(ArchiveOutcomeKind.GitError, null, step, null, null, null, null, null, false, null);

    public static ArchiveOutcome Archived(string item, string sidecar, string inboxPath, string line, string? sha, bool pushed, string? note) =>
        new(ArchiveOutcomeKind.Archived, null, null, item, sidecar, inboxPath, line, sha, pushed, note);
}
