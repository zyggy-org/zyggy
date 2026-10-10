using System.Reflection;

using Zyggy.Core.Memory;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Memory;

public sealed class MemoryPathsTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "zyggy-ut-paths");
    private static readonly MemoryPaths Paths = new(Root, MemoryTree.Alice);
    private static readonly string Principal = Path.Combine(Root, "acme", "alice");

    [Fact]
    public void File_SideCategorySlug_IsUnderPrincipalDirectory()
    {
        // Act
        var path = Paths.File(MemorySide.Business, CategoryName.Parse("clients"), Slug.Parse("acme-corp"));

        // Assert
        Paths.PrincipalDirectory.Should().Be(Principal);
        path.Should().Be(Path.Combine(Principal, "business", "clients", "acme-corp.md"));
        Paths.CategoryIndex(MemorySide.Private, CategoryName.Parse("people")).Should().Be(Path.Combine(Principal, "private", "people", "_index.md"));
        Paths.Daily(new DateOnly(2026, 9, 30)).Should().Be(Path.Combine(Principal, "daily", "2026-09-30.md"));
        Paths.DailyMonth(2026, 8).Should().Be(Path.Combine(Principal, "daily", "2026-08.md"));
        Paths.Ledger.Should().Be(Path.Combine(Principal, ".dream", "ledger.json"));
        Paths.Quarantine.Should().Be(Path.Combine(Principal, ".dream", "quarantine.md"));
        Paths.Pending.Should().Be(Path.Combine(Principal, ".dream", "pending.json"));
        Paths.Profile.Should().Be(Path.Combine(Principal, "profile.md"));
        Paths.Relative(path).Should().Be("business/clients/acme-corp.md");
    }

    [Theory]
    [InlineData("../x.md", MemoryPathRefusal.Traversal)]
    [InlineData("/etc/passwd", MemoryPathRefusal.Absolute)]
    [InlineData("C:\\x", MemoryPathRefusal.Absolute)]
    [InlineData("\\\\server\\share\\x.md", MemoryPathRefusal.Absolute)]
    [InlineData("private/../../bob/x.md", MemoryPathRefusal.Traversal)]
    [InlineData("../../acme/bob/profile.md", MemoryPathRefusal.OutsidePrincipal)]
    [InlineData("../../globex/alice/profile.md", MemoryPathRefusal.OutsidePrincipal)]
    [InlineData("private/Areas/x.md", MemoryPathRefusal.InvalidSegment)]
    [InlineData("private/areas/Bad_Slug.md", MemoryPathRefusal.InvalidSegment)]
    [InlineData("work/areas/x.md", MemoryPathRefusal.InvalidSegment)]
    [InlineData("private/areas/deep/x.md", MemoryPathRefusal.InvalidSegment)]
    [InlineData("./profile.md", MemoryPathRefusal.InvalidSegment)]
    [InlineData("inbox//x.md", MemoryPathRefusal.InvalidSegment)]
    public void TryResolve_Refused_ReturnsReason(string relative, MemoryPathRefusal reason)
    {
        // Act
        var resolution = Paths.TryResolve(relative);

        // Assert
        resolution.Succeeded.Should().BeFalse();
        resolution.Refusal.Should().Be(reason);
        resolution.FullPath.Should().BeNull();
    }

    [Theory]
    [InlineData("profile.md", MemoryArea.Identity)]
    [InlineData("preferences.md", MemoryArea.Identity)]
    [InlineData("agents.md", MemoryArea.Agents)]
    [InlineData("private/areas/marathon.md", MemoryArea.Durable)]
    [InlineData("business/clients/acme-corp.md", MemoryArea.Durable)]
    [InlineData("business/clients/_index.md", MemoryArea.CategoryIndex)]
    [InlineData("daily/2026-09-30.md", MemoryArea.Daily)]
    [InlineData("inbox/m365-mail-backfill-2026-10-03.md", MemoryArea.Inbox)]
    [InlineData("auto/MEMORY.md", MemoryArea.Auto)]
    [InlineData(".dream/ledger.json", MemoryArea.Dream)]
    [InlineData("areas/work-redis.md", MemoryArea.Legacy)]
    [InlineData("people/carol.md", MemoryArea.Legacy)]
    [InlineData("topics/tea.md", MemoryArea.Legacy)]
    [InlineData("README.md", MemoryArea.Other)]
    [InlineData("business\\clients\\acme-corp.md", MemoryArea.Durable)]
    [InlineData("archive/zyggy/quote-2026.md", MemoryArea.ArchiveSidecar)]
    [InlineData("archive/zyggy/quote-2026.pdf", MemoryArea.ArchiveItem)]
    public void TryResolve_Valid_ClassifiesArea(string relative, MemoryArea area)
    {
        // Act
        var resolution = Paths.TryResolve(relative);

        // Assert
        resolution.Succeeded.Should().BeTrue();
        resolution.Area.Should().Be(area);
        resolution.FullPath.Should().StartWith(Principal + Path.DirectorySeparatorChar);
        resolution.RelativePath.Should().Be(relative.Replace('\\', '/'));
    }

    [Fact]
    public void TryResolve_EveryArea_HasAValidRow()
    {
        // Assert: TryResolve_Valid_ClassifiesArea covers every member.
        Enum.GetValues<MemoryArea>().Should().HaveCount(12);
    }

    [Theory]
    [InlineData("archive", MemoryArea.Other, null)]
    [InlineData("archive/zyggy", MemoryArea.Other, null)]
    [InlineData("archive/zyggy/quote.txt", MemoryArea.ArchiveItem, null)]
    [InlineData("archive/zyggy/quote.png", MemoryArea.ArchiveItem, null)]
    [InlineData("archive/zyggy/quote.jpg", MemoryArea.ArchiveItem, null)]
    [InlineData("archive/zyggy/quote.gif", MemoryArea.ArchiveItem, null)]
    [InlineData("archive/zyggy/quote.pdf", MemoryArea.ArchiveItem, null)]
    [InlineData("archive/zyggy/quote.md", MemoryArea.ArchiveSidecar, null)]
    [InlineData("archive/zyggy/quote.docx", null, MemoryPathRefusal.InvalidSegment)]
    [InlineData("archive/zyggy/sub/x.pdf", null, MemoryPathRefusal.InvalidSegment)]
    [InlineData("archive/Bad Slug/x.pdf", null, MemoryPathRefusal.InvalidSegment)]
    [InlineData("archive/zyggy/quote", null, MemoryPathRefusal.InvalidSegment)]
    [InlineData("archive/zyggy/_index.md", null, MemoryPathRefusal.InvalidSegment)]
    [InlineData("archive/x.md", null, MemoryPathRefusal.InvalidSegment)]
    [InlineData("archive/zyggy/quote.PNG", null, MemoryPathRefusal.InvalidSegment)]
    [InlineData("archive/../profile.md", null, MemoryPathRefusal.Traversal)]
    [InlineData("/archive/zyggy/x.pdf", null, MemoryPathRefusal.Absolute)]
    [InlineData("../../acme/bob/archive/zyggy/x.pdf", null, MemoryPathRefusal.OutsidePrincipal)]
    public void TryResolve_ArchivePath_ClassifiesOrRefuses(string relative, MemoryArea? area, MemoryPathRefusal? refusal)
    {
        // Act
        var resolution = Paths.TryResolve(relative);

        // Assert
        resolution.Succeeded.Should().Be(area is not null);
        resolution.Refusal.Should().Be(refusal);
        if (area is { } expected)
        {
            resolution.Area.Should().Be(expected);
        }
    }

    [Fact]
    public void ArchiveBuilders_AreUnderPrincipalAndRelativeRoundTrips()
    {
        // Arrange
        var project = Slug.Parse("zyggy");
        var slug = Slug.Parse("quote-2026");

        // Act
        var directory = Paths.ArchiveDirectory;
        var projectDirectory = Paths.ArchiveProject(project);
        var item = Paths.ArchiveItem(project, slug, ArchiveMediaType.Jpeg);
        var sidecar = Paths.ArchiveSidecar(project, slug);

        // Assert
        directory.Should().Be(Path.Combine(Principal, "archive"));
        projectDirectory.Should().Be(Path.Combine(Principal, "archive", "zyggy"));
        item.Should().Be(Path.Combine(Principal, "archive", "zyggy", "quote-2026.jpg"));
        sidecar.Should().Be(Path.Combine(Principal, "archive", "zyggy", "quote-2026.md"));
        Paths.Relative(item).Should().Be("archive/zyggy/quote-2026.jpg");
        Paths.Relative(sidecar).Should().Be("archive/zyggy/quote-2026.md");
        Paths.TryResolve(Paths.Relative(item)).Area.Should().Be(MemoryArea.ArchiveItem);
        Paths.TryResolve(Paths.Relative(sidecar)).Area.Should().Be(MemoryArea.ArchiveSidecar);
        Paths.TryResolve(Paths.Relative(projectDirectory)).Area.Should().Be(MemoryArea.Other);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("private/areas/a\u0000b.md")]
    [InlineData("inbox/\u0007.md")]
    [InlineData("a:b")]
    [InlineData("..")]
    [InlineData("/")]
    public void TryResolve_NeverThrows_ForArbitraryInput(string? relative)
    {
        // Act
        var act = () => Paths.TryResolve(relative);

        // Assert
        act.Should().NotThrow().Which.Succeeded.Should().BeFalse();
    }

    [Fact]
    public void PublicSurface_EveryPathMethodNeedsPrincipal()
    {
        // Act
        var constructors = typeof(MemoryPaths).GetConstructors();
        var statics = typeof(MemoryPaths).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.ReturnType == typeof(string));

        // Assert
        constructors.Should().ContainSingle().Which.GetParameters().Should().Contain(p => p.ParameterType == typeof(Principal));
        statics.Should().BeEmpty();
    }

    [Fact]
    public void Relative_PathOutsidePrincipal_Throws()
    {
        // Act
        var act = () => Paths.Relative(Path.Combine(Root, "acme", "bob", "profile.md"));

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void InboxFile_PlainName_IsInInbox()
    {
        // Act
        var path = Paths.InboxFile("remember-2026-09-30.md");

        // Assert
        path.Should().Be(Path.Combine(Principal, "inbox", "remember-2026-09-30.md"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/b.md")]
    [InlineData("a\\b.md")]
    [InlineData("../profile.md")]
    public void InboxFile_NotAPlainName_Throws(string fileName)
    {
        // Act
        var act = () => Paths.InboxFile(fileName);

        // Assert
        act.Should().Throw<ArgumentException>();
    }
}
