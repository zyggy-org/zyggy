using System.Text;

using Zyggy.Core.Memory;
using Zyggy.Core.Memory.Mirror;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Memory.Mirror;

/// <summary>
/// The snapshot Central builds for one machine (spec 47, slice 1): the reach, the never-copied areas, sidecars only under
/// <c>archive/</c>, symbolic links skipped, a file with a secret or contact hit withheld whole and its path logged once.
/// </summary>
public sealed class MemoryMirrorSnapshotTests : IDisposable
{
    private static readonly MirrorSource Source = new("0123456789abcdef0123456789abcdef01234567", new DateOnly(2026, 11, 17));

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly MachineName WorkLaptop = MachineName.Parse("work-laptop");

    private static readonly SecretPatterns Secrets =
        SecretPatterns.Load(Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt")).Patterns!;

    // A token-shaped canary: built at run time so no source file carries a whole token.
    private static readonly string Canary = "ghp_" + "Mirror7Canary4Line2Never9Shown";

    private readonly RecordingLogger<MirrorSnapshotBuilder> _logger = new();

    private readonly MemoryTree _tree = new(
        ("profile.md", "---\nname: Alice\n---\n- Lives in Ghent\n"),
        ("preferences.md", "- Short answers\n"),
        ("agents.md", "- Be brief\n"),
        ("private/health/_index.md", "Health\n"),
        ("private/health/dentist.md", "- Check-up in March\n"),
        ("business/clients/_index.md", "Clients\n"),
        ("business/clients/acme-corp.md", "- Renewal in June\n"),
        ("daily/2026-11-16.md", "- Met the roofer\n"),
        ("inbox/remember-2026-11-16.md", "- raw note\n"),
        ("auto/learned.md", "- auto fact\n"),
        (".dream/ledger.json", "{}\n"),
        ("work/notes.md", "- never synced\n"),
        ("notes.md", "- loose file at the top\n"));

    public MemoryMirrorSnapshotTests()
    {
        _tree.Write("archive/house/roof-quote.md", Sidecar("roof-quote", "house"));
        _tree.Write("archive/house/roof-quote.pdf", "%PDF-1.7 bytes");
        _tree.Write("archive/house/stray.md", "- not a sidecar\n");
    }

    public void Dispose() => _tree.Dispose();

    // AC-1: Given a memory tree with every top-level area, the default-reach snapshot holds exactly MIRROR.md and the files under the
    // identity files, private/, business/, daily/ and the archive sidecars; nothing from inbox/, auto/, .dream/ or archived item files.
    [Fact]
    public void Build_DefaultReach_ExactFileSet()
    {
        // Act
        var snapshot = Build(HomeLaptop, MirrorReach.Default);

        // Assert
        snapshot.Files.Keys.Should().BeEquivalentTo(
            "MIRROR.md",
            "acme/alice/profile.md",
            "acme/alice/preferences.md",
            "acme/alice/agents.md",
            "acme/alice/private/health/_index.md",
            "acme/alice/private/health/dentist.md",
            "acme/alice/business/clients/_index.md",
            "acme/alice/business/clients/acme-corp.md",
            "acme/alice/daily/2026-11-16.md",
            "acme/alice/archive/house/roof-quote.md");
        snapshot.Withheld.Should().BeEmpty();
    }

    // AC-1: the copied bytes are the memory file's bytes.
    [Fact]
    public void Build_DefaultReach_CopiesBytesUnchanged()
    {
        // Act
        var snapshot = Build(HomeLaptop, MirrorReach.Default);

        // Assert
        Encoding.UTF8.GetString(snapshot.Files["acme/alice/business/clients/acme-corp.md"]).Should().Be("- Renewal in June\n");
    }

    // AC-2: Given the same tree, the snapshot for a machine with reach business/ holds exactly MIRROR.md and the files under business/.
    [Fact]
    public void Build_BusinessReach_ExactFileSet()
    {
        // Act
        var snapshot = Build(WorkLaptop, MirrorReach.Of(["business/"]));

        // Assert
        snapshot.Files.Keys.Should().BeEquivalentTo(
            "MIRROR.md",
            "acme/alice/business/clients/_index.md",
            "acme/alice/business/clients/acme-corp.md");
    }

    // AC-2: a symbolic link under business/ (file or folder) is skipped.
    [Fact]
    public void Build_BusinessReach_SymbolicLinksSkipped()
    {
        // Arrange
        try
        {
            File.CreateSymbolicLink(_tree.Full("business/clients/link.md"), _tree.Full("private/health/dentist.md"));
            Directory.CreateSymbolicLink(_tree.Full("business/health"), _tree.Full("private/health"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("symbolic links need a privilege on this machine");
        }

        // Act
        var snapshot = Build(WorkLaptop, MirrorReach.Of(["business/"]));

        // Assert
        snapshot.Files.Keys.Should().BeEquivalentTo(
            "MIRROR.md",
            "acme/alice/business/clients/_index.md",
            "acme/alice/business/clients/acme-corp.md");
    }

    // AC-1: a reach that names a never-copied area still copies nothing from it.
    [Fact]
    public void Build_ReachNamingNeverCopiedAreas_CopiesNothingFromThem()
    {
        // Act
        var snapshot = Build(HomeLaptop, MirrorReach.Of(["inbox/", "auto/", ".dream/", "archive/"]));

        // Assert
        snapshot.Files.Keys.Should().BeEquivalentTo("MIRROR.md", "acme/alice/archive/house/roof-quote.md");
    }

    // AC-24: an archive/<project>/ folder with an item file and its .md sidecar yields only the sidecar in the default-reach snapshot
    // and nothing in the business/ snapshot.
    [Fact]
    public void Build_ArchiveFolder_SidecarOnlyInDefaultNothingInBusiness()
    {
        // Act
        var home = Build(HomeLaptop, MirrorReach.Default);
        var work = Build(WorkLaptop, MirrorReach.Of(["business/"]));

        // Assert
        home.Files.Keys.Where(k => k.StartsWith("acme/alice/archive/", StringComparison.Ordinal))
            .Should().Equal("acme/alice/archive/house/roof-quote.md");
        work.Files.Keys.Should().NotContain(k => k.Contains("/archive/", StringComparison.Ordinal));
    }

    // AC-4: a file with a secret-pattern line is absent from the snapshot and listed as withheld; the log names the path once and
    // never the line.
    [Fact]
    public void Build_SecretLine_FileWithheldPathLoggedNeverTheLine()
    {
        // Arrange
        _tree.Write("business/clients/acme-corp.md", "- Renewal in June\n- token " + Canary + "\n");

        // Act
        var snapshot = Build(WorkLaptop, MirrorReach.Of(["business/"]));

        // Assert
        snapshot.Files.Keys.Should().NotContain("acme/alice/business/clients/acme-corp.md");
        snapshot.Withheld.Should().Equal("acme/alice/business/clients/acme-corp.md");
        _logger.Entries.Should().ContainSingle(e => e.Message.Contains("acme/alice/business/clients/acme-corp.md", StringComparison.Ordinal));
        _logger.Messages.Should().NotContain(m => m.Contains(Canary, StringComparison.Ordinal) || m.Contains("Renewal", StringComparison.Ordinal));
        snapshot.Files.Values.Should().NotContain(bytes => Encoding.UTF8.GetString(bytes).Contains(Canary, StringComparison.Ordinal));
    }

    // AC-4: a contact-detail line (a phone number) withholds the file the same way.
    [Fact]
    public void Build_ContactLine_FileWithheld()
    {
        // Arrange
        _tree.Write("private/health/dentist.md", "- Check-up in March, call +32 470 12 34 56\n");

        // Act
        var snapshot = Build(HomeLaptop, MirrorReach.Default);

        // Assert
        snapshot.Files.Keys.Should().NotContain("acme/alice/private/health/dentist.md");
        snapshot.Withheld.Should().Equal("acme/alice/private/health/dentist.md");
        _logger.Messages.Should().NotContain(m => m.Contains("470", StringComparison.Ordinal));
    }

    // AC-4: an earlier copy of a withheld file is removed by the next snapshot: the change list names it as removed and withheld.
    [Fact]
    public void Changes_EarlierCopyOfWithheldFile_ListedRemoved()
    {
        // Arrange
        var earlier = Build(WorkLaptop, MirrorReach.Of(["business/"]));
        _tree.Write("business/clients/acme-corp.md", "- token " + Canary + "\n");
        _tree.Write("business/clients/new-client.md", "- Signed\n");
        _tree.Write("business/clients/_index.md", "Clients and prospects\n");
        var next = Build(WorkLaptop, MirrorReach.Of(["business/"]));

        // Act
        var changes = MirrorChanges.Between(earlier.Files, next);

        // Assert
        changes.Added.Should().Equal("acme/alice/business/clients/new-client.md");
        changes.Changed.Should().Equal("acme/alice/business/clients/_index.md");
        changes.Removed.Should().Equal("acme/alice/business/clients/acme-corp.md");
        changes.Withheld.Should().Equal("acme/alice/business/clients/acme-corp.md");
    }

    // AC-4: an unchanged tree gives no change (MIRROR.md is compared like any file, so a new source revision alone is a change).
    [Fact]
    public void Changes_SameTreeSameSource_Empty()
    {
        // Arrange
        var earlier = Build(HomeLaptop, MirrorReach.Default);
        var next = Build(HomeLaptop, MirrorReach.Default);

        // Act
        var changes = MirrorChanges.Between(earlier.Files, next);

        // Assert
        changes.IsEmpty.Should().BeTrue();
    }

    private MirrorSnapshot Build(MachineName machine, MirrorReach reach) =>
        new MirrorSnapshotBuilder(Secrets, _logger).Build(_tree.Paths, machine, reach, Source);

    private static string Sidecar(string slug, string project) =>
        MemoryFileWriter.Render(new ArchiveSidecar(
            "Roof quote", "Roof repair quote", Slug.Parse(project), ArchiveMediaType.Pdf, 14, "5a838678058f6de375e8635b5f2fea47a4e5f07cb1a882a44b10f39abc6f34ff",
            new DateOnly(2026, 9, 30), slug + ".pdf", new DateOnly(2026, 9, 30)).ToMemoryFile());
}
