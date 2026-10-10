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

    /// <summary>
    /// Every archived item in ordinal path order (spec 37 AC-18): sidecars paired with items by slug, <c>indexed</c> when any <c>.md</c>
    /// file under a side directory contains the item's path, <c>orphan</c> when one of the two files is missing. Reads files only; never git.
    /// </summary>
    public IReadOnlyList<ArchiveListRow> List(ArchiveListRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Directory.Exists(paths.ArchiveDirectory))
        {
            return [];
        }

        var projects = Directory.EnumerateDirectories(paths.ArchiveDirectory)
            .Select(Path.GetFileName)
            .Where(name => Slug.TryParse(name, out _))
            .Select(name => Slug.Parse(name!))
            .Where(project => request.Project is null || project == request.Project);
        var sideTexts = SideFileTexts();
        var rows = new List<ArchiveListRow>();
        foreach (var project in projects)
        {
            foreach (var group in ArchiveFiles(project).GroupBy(f => f.Slug))
            {
                var item = group.FirstOrDefault(f => f.Type is not null);
                var sidecarFile = group.FirstOrDefault(f => f.Type is null);
                var sidecar = sidecarFile is null ? null : ArchiveSidecar.TryParse(MemoryFileReader.Read(sidecarFile.FullPath));
                var itemRelative = item is null ? null : paths.Relative(item.FullPath);
                var state = item is null || sidecar is null ? ArchiveIndexState.Orphan
                    : sideTexts.Any(text => text.Contains(itemRelative!, StringComparison.Ordinal)) ? ArchiveIndexState.Indexed
                    : ArchiveIndexState.Unindexed;
                rows.Add(new ArchiveListRow(
                    itemRelative,
                    sidecarFile is null ? null : paths.Relative(sidecarFile.FullPath),
                    project,
                    group.Key,
                    item is null ? null : sidecar?.MediaType ?? item.Type,
                    item is null ? null : sidecar?.SizeBytes ?? new FileInfo(item.FullPath).Length,
                    sidecar?.Name,
                    sidecar?.Description,
                    sidecar?.Archived,
                    state));
            }
        }

        return rows
            .Where(row => !request.Unindexed || row.State == ArchiveIndexState.Unindexed)
            .OrderBy(row => row.Path, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// <c>archive remove</c> (spec 37 AC-19): the git preflight, <c>git rm</c> of the item and sidecar that exist, one <c>[stated]</c>
    /// <c>Removed archived item …</c> line, one <c>commit --only</c> of the removed paths pushed with the one-rebase rule. The fact line in
    /// the project's file is left to the dream. Neither file present → <see cref="ArchiveRefusal.NotFound"/>.
    /// </summary>
    public async Task<ArchiveOutcome> RemoveAsync(ArchiveRef reference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var preflight = await publisher.PreflightAsync(repository, cancellationToken).ConfigureAwait(false);
        if (preflight.Detail is not null)
        {
            return ArchiveOutcome.GitError(preflight.Detail);
        }

        var files = ArchiveFiles(reference.Project).Where(f => f.Slug == reference.Slug).ToList();
        var item = files.FirstOrDefault(f => f.Type is not null);
        var sidecarFile = files.FirstOrDefault(f => f.Type is null);
        if (item is null && sidecarFile is null)
        {
            return ArchiveOutcome.Refused(ArchiveRefusal.NotFound, null);
        }

        // Plan assumption A4: a missing sidecar gives the name "-"; the extension comes from the item, else from the sidecar.
        var sidecar = sidecarFile is null ? null : ArchiveSidecar.TryParse(MemoryFileReader.Read(sidecarFile.FullPath));
        var extension = item is not null ? Path.GetExtension(item.FullPath)[1..] : sidecar?.ItemExtension ?? "md";
        var itemRelative = item is null ? null : paths.Relative(item.FullPath);
        var sidecarRelative = sidecarFile is null ? null : paths.Relative(sidecarFile.FullPath);
        var repoPaths = ArchiveCommitMessage.RepoPaths(paths.Principal, new[] { itemRelative, sidecarRelative }.OfType<string>().ToArray());

        var rm = await publisher.RemoveAsync(repository, repoPaths, cancellationToken).ConfigureAwait(false);
        if (rm is not null)
        {
            return ArchiveOutcome.GitError(rm);
        }

        var remember = new RememberService(paths, zone, clock, IndexLinePatterns ?? secrets)
            .Remember(new RememberRequest("stated", $" (project:{reference.Project.Value})", string.Empty, ArchiveIndexLine.RemovedFact(reference.Project, reference.Slug, extension, sidecar?.Name ?? "-")));
        if (remember.RefusedBy is not null)
        {
            return ArchiveOutcome.Refused(ArchiveRefusal.SecretPattern, $"{remember.RefusedBy} (line)");
        }

        var message = ArchiveCommitMessage.Remove(reference.Project, reference.Slug, extension);
        var publish = await publisher.CommitAndPushAsync(repository, preflight.Branch!, message, [], repoPaths, cancellationToken).ConfigureAwait(false);
        return publish.Committed
            ? ArchiveOutcome.Removed(itemRelative, sidecarRelative, remember.Path!, remember.Line!, publish.Sha, publish.Pushed)
            : ArchiveOutcome.GitError(publish.GitStep!);
    }

    // The files directly under archive/<project>/ named <slug>.md (sidecar, Type null) or <slug>.<item extension> (item).
    private IEnumerable<ArchiveFile> ArchiveFiles(Slug project)
    {
        var directory = paths.ArchiveProject(project);
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        foreach (var full in Directory.EnumerateFiles(directory))
        {
            var name = Path.GetFileNameWithoutExtension(full);
            var extension = Path.GetExtension(full).TrimStart('.');
            if (!Slug.TryParse(name, out var slug))
            {
                continue;
            }

            if (extension == "md")
            {
                yield return new ArchiveFile(full, slug, null);
            }
            else if (ItemType(extension) is { } type)
            {
                yield return new ArchiveFile(full, slug, type);
            }
        }
    }

    // An item's type from its stored extension alone (an orphan item has no sidecar); txt reads as text/plain.
    private static ArchiveMediaType? ItemType(string extension) => extension switch
    {
        "txt" => ArchiveMediaType.TextPlain,
        "pdf" => ArchiveMediaType.Pdf,
        "png" => ArchiveMediaType.Png,
        "jpg" => ArchiveMediaType.Jpeg,
        "gif" => ArchiveMediaType.Gif,
        _ => null,
    };

    private List<string> SideFileTexts() =>
        Enum.GetValues<MemorySide>()
            .Select(paths.Side)
            .Where(Directory.Exists)
            .SelectMany(side => Directory.EnumerateFiles(side, "*.md", SearchOption.AllDirectories))
            .Select(File.ReadAllText)
            .ToList();

    private sealed record ArchiveFile(string FullPath, Slug Slug, ArchiveMediaType? Type);

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
