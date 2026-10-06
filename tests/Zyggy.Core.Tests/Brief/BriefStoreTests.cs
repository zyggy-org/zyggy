using System.Runtime.Versioning;
using System.Text;

namespace Zyggy.Core.Tests.Brief;

public sealed class BriefStoreTests : IDisposable
{
    private readonly BriefFixture _f = new();

    public static bool IsLinux => OperatingSystem.IsLinux();

    public void Dispose() => _f.Dispose();

    [Fact]
    public void WriteAtomically_LeavesNoTempFile_ContentExact()
    {
        // Act
        _f.Store.WriteAtomically(_f.Paths.Markdown(BriefFixture.Today), Encoding.UTF8.GetBytes("x\n"));

        // Assert
        File.ReadAllText(_f.Paths.Markdown(BriefFixture.Today)).Should().Be("x\n");
        Directory.EnumerateFiles(_f.Paths.Directory, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public void LastShown_RoundTrips_AbsentOrGarbageIsNull()
    {
        // Arrange
        _f.Store.ReadLastShown().Should().BeNull();

        // Act
        _f.Store.WriteLastShown(BriefFixture.Today);

        // Assert
        File.ReadAllText(_f.Paths.LastShown).Should().Be("2026-10-06\n");
        _f.Store.ReadLastShown().Should().Be(BriefFixture.Today);
        _f.WriteLastShown("yesterday\n");
        _f.Store.ReadLastShown().Should().BeNull();
    }

    [Fact]
    public void BriefDates_OnlyMarkdownBriefs_SortedOldestFirst()
    {
        // Arrange
        File.WriteAllText(_f.Paths.Markdown(new DateOnly(2026, 10, 5)), "b");
        File.WriteAllText(_f.Paths.Markdown(new DateOnly(2026, 10, 3)), "a");
        File.WriteAllText(_f.Paths.Sidecar(new DateOnly(2026, 10, 4)), "{}");
        File.WriteAllText(Path.Combine(_f.Paths.Directory, "brief-2026-10-02.md.tmp"), "t");

        // Act
        var dates = _f.Store.BriefDates();

        // Assert
        dates.Should().Equal(new DateOnly(2026, 10, 3), new DateOnly(2026, 10, 5));
    }

    [Fact]
    public void Generated_SidecarGenerated_ElseFileTime()
    {
        // Arrange
        _f.WriteGoldenBrief(BriefFixture.Today);
        File.WriteAllText(_f.Paths.Markdown(new DateOnly(2026, 10, 5)), "x");

        // Act
        var fromSidecar = _f.Store.Generated(BriefFixture.Today);
        var fromFile = _f.Store.Generated(new DateOnly(2026, 10, 5));

        // Assert
        fromSidecar.Should().Be(new DateTimeOffset(2026, 10, 6, 4, 31, 0, TimeSpan.Zero));
        fromFile.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "Unix file modes: Linux only")]
    [SupportedOSPlatform("linux")]
    public void WriteAtomically_OnLinux_File0600Directory0700()
    {
        // Arrange
        Directory.Delete(_f.Paths.Directory, recursive: true);

        // Act
        _f.Store.WriteLastShown(BriefFixture.Today);

        // Assert
        File.GetUnixFileMode(_f.Paths.LastShown).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.GetUnixFileMode(_f.Paths.Directory).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
