using Zyggy.Core.Brief;

namespace Zyggy.Core.Tests.Brief;

public sealed class BriefPathsTests
{
    [Fact]
    public void Directory_StateDirSet_BriefUnderIt()
    {
        // Act
        var paths = new BriefPaths(new Dictionary<string, string?> { ["ZYGGY_STATE_DIR"] = "/state", ["HOME"] = "/home/x" });

        // Assert
        paths.Directory.Should().Be(Path.Join("/state", "brief"));
        paths.LastShown.Should().Be(Path.Join("/state", "brief", "last-shown"));
        paths.IdeasLog.Should().Be(Path.Join("/state", "brief", "ideas.jsonl"));
        paths.RunsDirectory.Should().Be(Path.Join("/state", "brief", "runs"));
    }

    [Fact]
    public void Directory_NoStateDir_HomeLocalStateZyggyBrief()
    {
        // Act
        var paths = new BriefPaths(new Dictionary<string, string?> { ["HOME"] = "/home/x" });

        // Assert
        paths.Directory.Should().Be(Path.Join("/home/x", ".local", "state", "zyggy", "brief"));
    }

    [Fact]
    public void Markdown_Sidecar_FileNamesRoundTrip()
    {
        // Arrange
        var paths = new BriefPaths(new Dictionary<string, string?> { ["ZYGGY_STATE_DIR"] = "/state" });
        var date = new DateOnly(2026, 10, 6);

        // Act
        var md = Path.GetFileName(paths.Markdown(date));
        var json = Path.GetFileName(paths.Sidecar(date));

        // Assert
        md.Should().Be("brief-2026-10-06.md");
        json.Should().Be("brief-2026-10-06.json");
        BriefPaths.TryParseDate(md, out var d1, out var k1).Should().BeTrue();
        (d1, k1).Should().Be((date, BriefFileKind.Markdown));
        BriefPaths.TryParseDate(json, out var d2, out var k2).Should().BeTrue();
        (d2, k2).Should().Be((date, BriefFileKind.Sidecar));
    }

    [Theory]
    [InlineData("last-shown")]
    [InlineData("brief-2026-10-06.md.tmp")]
    [InlineData("brief-2026-13-01.md")]
    [InlineData("brief-20261006.md")]
    [InlineData("ideas.jsonl")]
    [InlineData("brief-2026-10-06.txt")]
    public void TryParseDate_NotABriefFile_False(string name)
    {
        // Act
        var ok = BriefPaths.TryParseDate(name, out _, out _);

        // Assert
        ok.Should().BeFalse();
    }
}
