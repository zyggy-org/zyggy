using Zyggy.Core.Configuration;
using Zyggy.Core.Memory;

namespace Zyggy.Core.Bus;

/// <summary>A file that passed the laptop's checks: the bytes read once, their type, SHA-256, cleaned source name and the OneDrive / memory file name.</summary>
internal sealed record ItemCandidate(byte[] Bytes, ArchiveMediaType Type, string Sha256, string SourceName, string DriveName);

/// <summary>The outcome of <see cref="ItemChecks.Check"/>: a candidate, a refusal with its detail token (never item text), or a usage message (exit 2).</summary>
internal sealed record ItemCheckResult(ItemCandidate? Candidate, ArchiveRefusal? Refusal, string? Detail)
{
    /// <summary>Gets the usage message when the composed index line is too long; <see langword="null"/> otherwise.</summary>
    public string? UsageError { get; init; }
}

/// <summary>
/// The laptop's half of the archive checks (spec 41 AC-12..AC-15, decision 1 "no weaker checks"): the source against the laptop deny
/// list, the bytes read once, type and Office content, the laptop cap <c>archive.itemMaxBytes</c>, secrets on text lines, name and
/// description, contact details and the composed index line — <see cref="ArchiveChecks"/>' own code, without the checks only Central's
/// archive can answer (allowed types, project and total caps, slug taken).
/// </summary>
internal static class ItemChecks
{
    public static ItemCheckResult Check(ItemRequest request, ArchiveSettings settings, SourceDenyList deny, SecretPatterns secrets, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(deny);
        ArgumentNullException.ThrowIfNull(secrets);

        var source = ArchiveChecks.CheckSource(request.SourcePath, deny);
        if (source.Refusal is { } refused)
        {
            return new ItemCheckResult(null, refused, source.Detail);
        }

        var content = new ArchiveContentRequest(request.Project, request.Name, request.Description, request.Slug, source.SourceName!, source.Bytes!, settings.ItemMaxBytes);
        var checkedContent = ArchiveChecks.CheckContent(content, ArchiveCheckScope.Laptop, null, null, secrets, today);
        if (checkedContent.UsageError is { } usage)
        {
            return new ItemCheckResult(null, null, null) { UsageError = usage };
        }

        if (checkedContent.Candidate is not { } candidate)
        {
            return new ItemCheckResult(null, checkedContent.Refusal, checkedContent.Detail);
        }

        var driveName = request.Slug.Value + "." + ArchiveMediaTypeWire.Extension(candidate.Type);
        return new ItemCheckResult(new ItemCandidate(candidate.Bytes, candidate.Type, candidate.Sha256, candidate.SourceName, driveName), null, null);
    }
}
