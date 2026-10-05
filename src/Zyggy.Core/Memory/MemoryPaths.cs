using System.Globalization;
using System.Text.RegularExpressions;

using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Memory;

/// <summary>
/// Every path into one principal's memory tree <c>&lt;root&gt;/&lt;tenant&gt;/&lt;user&gt;/</c> (founding spec §7, §14).
/// The only place memory paths are built; nothing it returns lies outside the principal directory.
/// </summary>
public sealed partial class MemoryPaths
{
    private const string Separators = "/\\";

    private static readonly char[] ForbiddenCharacters = ['<', '>', ':', '"', '|', '?', '*', '/', '\\'];

    /// <summary>Creates the paths of <paramref name="principal"/> under <paramref name="root"/>.</summary>
    /// <param name="root">The memory root (the repository's working tree).</param>
    /// <param name="principal">The tenant and user whose tree this is.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="root"/> is empty.</exception>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    public MemoryPaths(string root, Principal principal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(principal);
        Principal = principal;
        RootDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        PrincipalDirectory = Path.Join(RootDirectory, principal.Tenant.Value, principal.User.Value);
    }

    /// <summary>Gets the principal whose tree this is.</summary>
    public Principal Principal { get; }

    /// <summary>Gets the absolute memory root.</summary>
    public string RootDirectory { get; }

    /// <summary>Gets the absolute principal directory <c>&lt;root&gt;/&lt;tenant&gt;/&lt;user&gt;</c>.</summary>
    public string PrincipalDirectory { get; }

    /// <summary>Gets <c>profile.md</c>.</summary>
    public string Profile => Join("profile.md");

    /// <summary>Gets <c>preferences.md</c>.</summary>
    public string Preferences => Join("preferences.md");

    /// <summary>Gets <c>agents.md</c>.</summary>
    public string Agents => Join("agents.md");

    /// <summary>Gets <c>inbox/</c>.</summary>
    public string InboxDirectory => Join("inbox");

    /// <summary>Gets <c>daily/</c>.</summary>
    public string DailyDirectory => Join("daily");

    /// <summary>Gets <c>auto/</c>.</summary>
    public string AutoDirectory => Join("auto");

    /// <summary>Gets <c>.dream/</c>.</summary>
    public string DreamDirectory => Join(".dream");

    /// <summary>Gets <c>.dream/ledger.json</c>.</summary>
    public string Ledger => Join(".dream", "ledger.json");

    /// <summary>Gets <c>.dream/quarantine.md</c>.</summary>
    public string Quarantine => Join(".dream", "quarantine.md");

    /// <summary>Gets <c>.dream/pending.json</c>.</summary>
    public string Pending => Join(".dream", "pending.json");

    /// <summary>Returns a file directly in <c>inbox/</c>, for example <c>remember-2026-09-30.md</c>.</summary>
    /// <param name="fileName">The file name: no directory separator, not empty, not <c>.</c> or <c>..</c>.</param>
    /// <returns>The absolute path.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="fileName"/> is not a plain file name.</exception>
    public string InboxFile(string fileName)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        if (fileName is "." or ".." || fileName.AsSpan().IndexOfAny(Separators) >= 0)
        {
            throw new ArgumentException($"'{fileName}' is not a plain file name.", nameof(fileName));
        }

        return Join("inbox", fileName);
    }

    /// <summary>Returns <c>daily/YYYY-MM-DD.md</c>.</summary>
    /// <param name="date">The local date.</param>
    /// <returns>The absolute path.</returns>
    public string Daily(DateOnly date) => Join("daily", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".md");

    /// <summary>Returns the monthly roll-up <c>daily/YYYY-MM.md</c>.</summary>
    /// <param name="year">The year.</param>
    /// <param name="month">The month, 1–12.</param>
    /// <returns>The absolute path.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for a month outside 1–12 or a year outside 1–9999.</exception>
    public string DailyMonth(int year, int month)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(month, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(month, 12);
        ArgumentOutOfRangeException.ThrowIfLessThan(year, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(year, 9999);
        return Join("daily", string.Create(CultureInfo.InvariantCulture, $"{year:0000}-{month:00}.md"));
    }

    /// <summary>Returns a side directory.</summary>
    /// <param name="side">The side.</param>
    /// <returns><c>private/</c> or <c>business/</c>.</returns>
    public string Side(MemorySide side) => Join(MemorySideWire.ToWire(side));

    /// <summary>Returns a category directory.</summary>
    /// <param name="side">The side.</param>
    /// <param name="category">The category.</param>
    /// <returns><c>&lt;side&gt;/&lt;category&gt;/</c>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="category"/> is null.</exception>
    public string Category(MemorySide side, CategoryName category)
    {
        ArgumentNullException.ThrowIfNull(category);
        return Join(MemorySideWire.ToWire(side), category.Value);
    }

    /// <summary>Returns a category's description file.</summary>
    /// <param name="side">The side.</param>
    /// <param name="category">The category.</param>
    /// <returns><c>&lt;side&gt;/&lt;category&gt;/_index.md</c>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="category"/> is null.</exception>
    public string CategoryIndex(MemorySide side, CategoryName category)
    {
        ArgumentNullException.ThrowIfNull(category);
        return Join(MemorySideWire.ToWire(side), category.Value, "_index.md");
    }

    /// <summary>Returns a memory file.</summary>
    /// <param name="side">The side.</param>
    /// <param name="category">The category.</param>
    /// <param name="slug">The file's slug.</param>
    /// <returns><c>&lt;side&gt;/&lt;category&gt;/&lt;slug&gt;.md</c>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="category"/> or <paramref name="slug"/> is null.</exception>
    public string File(MemorySide side, CategoryName category, Slug slug)
    {
        ArgumentNullException.ThrowIfNull(category);
        ArgumentNullException.ThrowIfNull(slug);
        return Join(MemorySideWire.ToWire(side), category.Value, slug.Value + ".md");
    }

    /// <summary>
    /// Resolves a path relative to the principal directory (<c>/</c> or <c>\</c> separators) and classifies it. Never throws:
    /// rooted paths, <c>..</c>, malformed segments, another principal's tree and symbolic links leading out are refused.
    /// </summary>
    /// <param name="relativePath">The relative path, for example <c>business/clients/acme-corp.md</c>.</param>
    /// <returns>The accepted full path and area, or the refusal.</returns>
    public MemoryPathResolution TryResolve(string? relativePath)
    {
        try
        {
            return Resolve(relativePath);
        }
#pragma warning disable CA1031 // The contract: a refusal, never an exception to the caller.
        catch (Exception)
#pragma warning restore CA1031
        {
            return MemoryPathResolution.Refuse(MemoryPathRefusal.InvalidSegment);
        }
    }

    /// <summary>Returns <paramref name="fullPath"/> relative to the principal directory with <c>/</c> separators.</summary>
    /// <param name="fullPath">An absolute path inside the principal directory.</param>
    /// <returns>The relative path.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="fullPath"/> is not inside the principal directory.</exception>
    public string Relative(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        var full = Path.GetFullPath(fullPath);
        return IsInside(full)
            ? Path.GetRelativePath(PrincipalDirectory, full).Replace('\\', '/')
            : throw new ArgumentException("The path is not inside the principal's memory directory.", nameof(fullPath));
    }

    private MemoryPathResolution Resolve(string? relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
        {
            return MemoryPathResolution.Refuse(MemoryPathRefusal.InvalidSegment);
        }

        if (Path.IsPathRooted(relativePath) || Separators.Contains(relativePath[0], StringComparison.Ordinal) || DriveLetter().IsMatch(relativePath))
        {
            return MemoryPathResolution.Refuse(MemoryPathRefusal.Absolute);
        }

        var segments = relativePath.Split(['/', '\\']);
        if (segments.Contains(".."))
        {
            return MemoryPathResolution.Refuse(IsOtherPrincipal(segments) ? MemoryPathRefusal.OutsidePrincipal : MemoryPathRefusal.Traversal);
        }

        if (!segments.All(IsValidSegment) || Classify(segments) is not { } area)
        {
            return MemoryPathResolution.Refuse(MemoryPathRefusal.InvalidSegment);
        }

        var full = Path.GetFullPath(Path.Join([PrincipalDirectory, .. segments]));
        if (!IsInside(full))
        {
            return MemoryPathResolution.Refuse(MemoryPathRefusal.OutsidePrincipal);
        }

        return LeavesThroughLink(segments)
            ? MemoryPathResolution.Refuse(MemoryPathRefusal.SymlinkEscape)
            : MemoryPathResolution.Accept(full, string.Join('/', segments), area);
    }

    // "../../<tenant>/<user>/..." names another principal's tree; any other ".." is plain traversal.
    private bool IsOtherPrincipal(string[] segments) =>
        segments.Length >= 5
        && segments[0] == ".." && segments[1] == ".."
        && !segments.Skip(2).Contains("..")
        && TenantId.TryParse(segments[2], out var tenant)
        && UserId.TryParse(segments[3], out var user)
        && new Principal(tenant, user) != Principal;

    private static bool IsValidSegment(string segment) =>
        segment.Length > 0
        && segment != "."
        && segment.Trim() == segment
        && !segment.Any(c => char.IsControl(c) || ForbiddenCharacters.Contains(c));

    private static MemoryArea? Classify(string[] segments)
    {
        var first = segments[0];
        if (segments.Length == 1)
        {
            return first switch
            {
                "profile.md" or "preferences.md" => MemoryArea.Identity,
                "agents.md" => MemoryArea.Agents,
                "work" => null,
                _ => MemoryArea.Other,
            };
        }

        if (MemorySideWire.TryFromWire(first, out _))
        {
            if (!CategoryName.TryParse(segments[1], out _) || segments.Length > 3)
            {
                return null;
            }

            if (segments.Length == 2)
            {
                return MemoryArea.Other;
            }

            var name = segments[2];
            if (name == "_index.md")
            {
                return MemoryArea.CategoryIndex;
            }

            return name.EndsWith(".md", StringComparison.Ordinal) && Slug.TryParse(name[..^3], out _) ? MemoryArea.Durable : null;
        }

        return first switch
        {
            "daily" => MemoryArea.Daily,
            "inbox" => MemoryArea.Inbox,
            "auto" => MemoryArea.Auto,
            ".dream" => MemoryArea.Dream,
            "areas" or "people" or "topics" => MemoryArea.Legacy,
            "work" => null,
            _ => MemoryArea.Other,
        };
    }

    private bool LeavesThroughLink(string[] segments)
    {
        var current = PrincipalDirectory;
        foreach (var segment in segments)
        {
            current = Path.Join(current, segment);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (!info.Exists || info.LinkTarget is null)
            {
                continue;
            }

            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target is null || !IsInside(Path.GetFullPath(target.FullName)))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsInside(string full) =>
        full.StartsWith(PrincipalDirectory + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private string Join(params string[] parts) => Path.Join([PrincipalDirectory, .. parts]);

    [GeneratedRegex("^[A-Za-z]:", RegexOptions.CultureInvariant)]
    private static partial Regex DriveLetter();
}
