namespace Zyggy.Core.Outlook;

/// <summary>
/// The pure rules of the COM access (spec 58 slice 2), kept apart from Outlook so they are tested: which folder names are Outlook's
/// well-known folders (<c>OlDefaultFolders</c>), how a folder path splits, and which mail is protected (AC-4: its body is never read).
/// </summary>
internal static class OutlookComRules
{
    private const string Rpmsg = "message.rpmsg";

    private static readonly Dictionary<string, int> WellKnown = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Deleted Items"] = 3,
        ["Outbox"] = 4,
        ["Sent Items"] = 5,
        ["Inbox"] = 6,
        ["Drafts"] = 16,
        ["Junk Email"] = 23,
    };

    /// <summary>Returns the <c>OlDefaultFolders</c> value of a well-known folder name, or <see langword="null"/> for any other name.</summary>
    /// <param name="name">The folder name as the owner wrote it (case-insensitive).</param>
    /// <returns>The value, or <see langword="null"/>.</returns>
    public static int? WellKnownFolder(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return WellKnown.TryGetValue(name, out var kind) ? kind : null;
    }

    /// <summary>Splits a folder path (<c>Inbox/Projects</c>, either separator) into its trimmed, non-empty segments.</summary>
    /// <param name="folderPath">The path.</param>
    /// <returns>The segments; empty for a path without one.</returns>
    public static IReadOnlyList<string> Segments(string folderPath)
    {
        ArgumentNullException.ThrowIfNull(folderPath);
        return folderPath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// Whether a mail is encrypted or rights-protected: an S/MIME message class other than clear-signed (<c>IPM.Note.SMIME</c>,
    /// not <c>IPM.Note.SMIME.MultipartSigned</c>), or an attachment named <c>message.rpmsg</c> (a rights-managed mail).
    /// </summary>
    /// <param name="messageClass">Outlook's <c>MessageClass</c>.</param>
    /// <param name="attachmentNames">The attachments' file names.</param>
    /// <returns>Whether the mail is protected.</returns>
    public static bool IsProtected(string messageClass, IReadOnlyList<string> attachmentNames)
    {
        ArgumentNullException.ThrowIfNull(messageClass);
        ArgumentNullException.ThrowIfNull(attachmentNames);
        var smime = messageClass.StartsWith("IPM.Note.SMIME", StringComparison.OrdinalIgnoreCase)
            && !messageClass.StartsWith("IPM.Note.SMIME.MultipartSigned", StringComparison.OrdinalIgnoreCase);
        return smime || attachmentNames.Any(name => string.Equals(name, Rpmsg, StringComparison.OrdinalIgnoreCase));
    }
}
