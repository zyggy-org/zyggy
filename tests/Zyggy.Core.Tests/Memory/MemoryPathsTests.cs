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
    [InlineData("areas/riziv-redis.md", MemoryArea.Legacy)]
    [InlineData("people/carol.md", MemoryArea.Legacy)]
    [InlineData("topics/tea.md", MemoryArea.Legacy)]
    [InlineData("README.md", MemoryArea.Other)]
    [InlineData("business\\clients\\acme-corp.md", MemoryArea.Durable)]
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
        Enum.GetValues<MemoryArea>().Should().HaveCount(10);
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
