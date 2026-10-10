namespace Zyggy.Core.Memory;

/// <summary>
/// <c>archive add</c> after its usage checks (spec 37 Behaviors "Nothing before everything"): the git preflight, the closed checks on the
/// bytes read once, then — only after the last check — the item and its sidecar written atomically, one <c>[stated]</c> index line through
/// <see cref="RememberService"/>, and one <c>commit --only</c> of the two archive paths pushed through <see cref="MemoryPublisher"/>.
/// A git failure after the write leaves the two files on disk and says so.
/// </summary>
internal sealed class ArchiveService(
    MemoryPaths paths,
    TimeZoneInfo zone,
    TimeProvider clock,
    SecretPatterns secrets,
    ArchiveOptions options,
    SourceDenyList deny,
    MemoryPublisher publisher,
    string repository)
{
    /// <summary>The patterns <see cref="RememberService"/> applies to the index line (default: the check-phase patterns); the check phase pre-scans the line, so a refusal there is unexpected.</summary>
    public SecretPatterns? IndexLinePatterns { get; init; }

    public async Task<ArchiveOutcome> AddAsync(ArchiveAddRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 1. Git must be able to commit before anything is read or written; a pending push of an earlier commit is not an error here.
        var preflight = await publisher.PreflightAsync(repository, cancellationToken).ConfigureAwait(false);
        if (preflight.Detail is not null)
        {
            return ArchiveOutcome.GitError(preflight.Detail);
        }

        // 2. The closed checks.
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).DateTime);
        var check = ArchiveChecks.Check(request, options, paths, deny, secrets, today);
        if (check.Refusal is { } refusal)
        {
            return ArchiveOutcome.Refused(refusal, check.Detail);
        }

        // 3. Item and sidecar, atomically.
        var candidate = check.Candidate!;
        var itemPath = paths.ArchiveItem(request.Project, request.Slug, candidate.Type);
        var sidecarPath = paths.ArchiveSidecar(request.Project, request.Slug);
        var size = candidate.Bytes.LongLength;
        cancellationToken.ThrowIfCancellationRequested();
        MemoryFileWriter.WriteAtomically(itemPath, candidate.Bytes);
        var sidecar = new ArchiveSidecar(request.Name, request.Description, request.Project, candidate.Type, size, candidate.Sha256, today, candidate.SourceName, today);
        MemoryFileWriter.Write(sidecarPath, sidecar.ToMemoryFile());

        // 4. The index line, byte-identical to `memory remember --scope project:<project> -- "<fact>"`.
        var remember = new RememberService(paths, zone, clock, IndexLinePatterns ?? secrets)
            .Remember(new RememberRequest("stated", $" (project:{request.Project.Value})", string.Empty, ArchiveIndexLine.AddedFact(request, candidate.Type, size)));
        if (remember.RefusedBy is not null)
        {
            return ArchiveOutcome.Refused(ArchiveRefusal.SecretPattern, $"{remember.RefusedBy} (line)");
        }

        // 5. One commit of exactly the two archive paths, pushed.
        var itemRelative = paths.Relative(itemPath);
        var sidecarRelative = paths.Relative(sidecarPath);
        var repoPaths = ArchiveCommitMessage.RepoPaths(paths.Principal, itemRelative, sidecarRelative);
        var message = ArchiveCommitMessage.Add(request.Project, request.Slug, sidecar.ItemExtension, candidate.Type, size, candidate.Sha256);
        var publish = await publisher.CommitAndPushAsync(repository, preflight.Branch!, message, repoPaths, repoPaths, cancellationToken).ConfigureAwait(false);
        if (!publish.Committed)
        {
            return ArchiveOutcome.GitError(publish.GitStep!);
        }

        var note = ProjectFileExists(request.Project) ? null : $"no memory file named {request.Project.Value} yet; the dream will create it";
        return ArchiveOutcome.Archived(itemRelative, sidecarRelative, remember.Path!, remember.Line!, publish.Sha, publish.Pushed, note);
    }

    // The verb does not look up sides: any <side>/<category>/<project>.md counts.
    private bool ProjectFileExists(Slug project) =>
        Enum.GetValues<MemorySide>()
            .Where(side => Directory.Exists(paths.Side(side)))
            .SelectMany(side => Directory.EnumerateDirectories(paths.Side(side))
                .Select(Path.GetFileName)
                .Where(name => CategoryName.TryParse(name, out _))
                .Select(name => paths.File(side, CategoryName.Parse(name!), project)))
            .Any(File.Exists);
}
