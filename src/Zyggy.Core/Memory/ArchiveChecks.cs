using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Zyggy.Core.Media;

namespace Zyggy.Core.Memory;

/// <summary>An item that passed every check: the bytes read once, their type, hash, the source's file name and the index line to append.</summary>
internal sealed record ArchiveCandidate(byte[] Bytes, ArchiveMediaType Type, string Sha256, string SourceName, string IndexLine);

/// <summary>The outcome of <see cref="ArchiveChecks.Check"/>: a candidate, or the refusal with its detail token (never item text).</summary>
internal sealed record ArchiveCheckResult(ArchiveCandidate? Candidate, ArchiveRefusal? Refusal, string? Detail)
{
    public static ArchiveCheckResult Refuse(ArchiveRefusal refusal, string? detail = null) => new(null, refusal, detail);
}

/// <summary>
/// The closed checks of <c>archive add</c> (spec 37 AC-7..AC-11), in the spec's order: source, bytes read once, type, size and caps,
/// secret patterns on text lines and on name and description, contact details on name and description, slug free.
/// </summary>
internal static class ArchiveChecks
{
    private const int SourceNameMax = 100;

    public static ArchiveCheckResult Check(
        ArchiveAddRequest request,
        ArchiveOptions options,
        MemoryPaths paths,
        SourceDenyList deny,
        SecretPatterns secrets,
        DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(deny);
        ArgumentNullException.ThrowIfNull(secrets);

        // 1. Source (AC-7).
        var source = request.SourcePath;
        if (Directory.Exists(source))
        {
            return ArchiveCheckResult.Refuse(ArchiveRefusal.SourceRefused, "not_regular_file");
        }

        if (deny.Covers(source) is { } covered)
        {
            return ArchiveCheckResult.Refuse(ArchiveRefusal.SourceRefused, covered);
        }

        var file = new FileInfo(source);
        if (!file.Exists)
        {
            return ArchiveCheckResult.Refuse(ArchiveRefusal.SourceRefused, "not_found");
        }

        if (file.LinkTarget is not null || HasLinkedAncestor(file.Directory) || !string.Equals(Path.GetFullPath(source), source, StringComparison.Ordinal))
        {
            return ArchiveCheckResult.Refuse(ArchiveRefusal.SourceRefused, "symlink");
        }

        // 2. Bytes, read once.
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ArchiveCheckResult.Refuse(ArchiveRefusal.SourceRefused, "not_regular_file");
        }

        if (bytes.Length == 0)
        {
            return ArchiveCheckResult.Refuse(ArchiveRefusal.Empty);
        }

        // 3. Type (AC-8).
        var extension = Path.GetExtension(source);
        var markdownName = extension.Equals(".md", StringComparison.OrdinalIgnoreCase) || extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase);
        if (MediaSniffer.Sniff(bytes, markdownName) is not { } wire || !ArchiveMediaTypeWire.TryFromWire(wire, out var type))
        {
            return ArchiveCheckResult.Refuse(ArchiveRefusal.TypeRefused);
        }

        if (!options.AllowedTypes.Contains(type))
        {
            return ArchiveCheckResult.Refuse(ArchiveRefusal.TypeNotAllowed, wire);
        }

        // 4. Size and caps (AC-9); existing bytes count items and sidecars (plan assumption A3).
        if (bytes.LongLength > options.ItemMaxBytes)
        {
            return ArchiveCheckResult.Refuse(ArchiveRefusal.TooLarge, string.Create(CultureInfo.InvariantCulture, $"{bytes.LongLength} > {options.ItemMaxBytes}"));
        }

        if (BytesUnder(paths.ArchiveProject(request.Project)) + bytes.LongLength > options.ProjectMaxBytes)
        {
            return ArchiveCheckResult.Refuse(ArchiveRefusal.ProjectCap);
        }

        if (BytesUnder(paths.ArchiveDirectory) + bytes.LongLength > options.TotalMaxBytes)
        {
            return ArchiveCheckResult.Refuse(ArchiveRefusal.TotalCap);
        }

        // 5. Secrets on text lines; secrets and contact details on name and description (AC-10, owner decision OQ-1 (b)).
        if (type is ArchiveMediaType.TextPlain or ArchiveMediaType.TextMarkdown
            && secrets.TryMatchLines(TextLines.Split(Encoding.UTF8.GetString(bytes)), out var lineName, out var lineNumber))
        {
            return ArchiveCheckResult.Refuse(ArchiveRefusal.SecretPattern, string.Create(CultureInfo.InvariantCulture, $"{lineName} (line {lineNumber})"));
        }

        if (secrets.TryMatch(request.Name, out var nameName))
        {
            return ArchiveCheckResult.Refuse(ArchiveRefusal.SecretPattern, $"{nameName} (name)");
        }

        if (secrets.TryMatch(request.Description, out var descriptionName))
        {
            return ArchiveCheckResult.Refuse(ArchiveRefusal.SecretPattern, $"{descriptionName} (description)");
        }

        if (ContactDetailPatterns.Contains(request.Name))
        {
            return ArchiveCheckResult.Refuse(ArchiveRefusal.ContactDetail, "name");
        }

        if (ContactDetailPatterns.Contains(request.Description))
        {
            return ArchiveCheckResult.Refuse(ArchiveRefusal.ContactDetail, "description");
        }

        // The composed line itself, so RememberService can never refuse after the files are written.
        var indexLine = ArchiveIndexLine.Added(today, request, type, bytes.LongLength);
        if (secrets.TryMatch(indexLine, out var lineMatch))
        {
            return ArchiveCheckResult.Refuse(ArchiveRefusal.SecretPattern, $"{lineMatch} (line)");
        }

        if (ContactDetailPatterns.Contains(indexLine))
        {
            return ArchiveCheckResult.Refuse(ArchiveRefusal.ContactDetail, "line");
        }

        // 6. Slug (AC-11): any archive/<project>/<slug>.* already present.
        var project = paths.ArchiveProject(request.Project);
        if (Directory.Exists(project) && Directory.EnumerateFiles(project, request.Slug.Value + ".*").Any())
        {
            return ArchiveCheckResult.Refuse(ArchiveRefusal.SlugTaken);
        }

        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return new ArchiveCheckResult(new ArchiveCandidate(bytes, type, sha, SourceName(source), indexLine), null, null);
    }

    private static bool HasLinkedAncestor(DirectoryInfo? directory)
    {
        for (var d = directory; d is not null; d = d.Parent)
        {
            if (d.LinkTarget is not null)
            {
                return true;
            }
        }

        return false;
    }

    private static long BytesUnder(string directory) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)
            : 0;

    private static string SourceName(string source)
    {
        var name = Path.GetFileName(source);
        var builder = new StringBuilder(name.Length);
        foreach (var c in name.Where(c => !char.IsControl(c)))
        {
            builder.Append(c);
        }

        var cleaned = builder.ToString();
        return cleaned.Length > SourceNameMax ? cleaned[..SourceNameMax] : cleaned;
    }
}
