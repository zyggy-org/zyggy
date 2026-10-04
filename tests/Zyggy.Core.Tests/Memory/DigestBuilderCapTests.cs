using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Memory;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Memory;

/// <summary>Caps (27 algorithms for identity and daily; the new index): every section stays within its byte cap.</summary>
public sealed partial class DigestBuilderCapTests
{
    private static readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero));

    private static int Bytes(string text) => Encoding.UTF8.GetByteCount(text);

    private static string[] Lines(string text) => text.TrimEnd('\n').Split('\n');

    private static string NthLast(string text, int n) => Lines(text)[^n];

    private static MemoryTree SidedWith(int files)
    {
        var tree = new MemoryTree(("agents.md", "- [stated] 2026-09-18: central owns memory.\n"),
            ("business/areas/_index.md", "---\nname: areas\ndescription: Professional projects\nupdated: 2026-09-30\n---\n"));
        for (var i = 0; i < files; i++)
        {
            var day = 1 + (i % 28);
            tree.Write(
                $"business/areas/gen-{i:000}.md",
                $"---\nname: gen-{i:000}\ndescription: generated index entry number {i:000} for the cap test\nupdated: 2026-09-{day:00}\n---\n- [stated] 2026-09-30: filler\n");
        }

        return tree;
    }

    [Fact]
    public void Build_IndexWith300Files_StaysWithinCapAndEndsWithMoreFilesLine()
    {
        // Arrange
        using var tree = SidedWith(300);

        // Act
        var output = new DigestBuilder(tree.Paths, new DigestOptions(), Clock).Build(DigestSection.Index, null);

        // Assert
        Bytes(output.Text).Should().BeLessThanOrEqualTo(6000);
        NthLast(output.Text, 1).Should().Be("</zyggy-memory-digest>");
        var more = MoreLine().Match(NthLast(output.Text, 2));
        more.Success.Should().BeTrue(NthLast(output.Text, 2));
        var kept = Lines(output.Text).Count(l => l.StartsWith("- business/areas/gen-", StringComparison.Ordinal));
        kept.Should().BeGreaterThan(0);
        (kept + int.Parse(more.Groups[1].Value, CultureInfo.InvariantCulture)).Should().Be(300);
        Lines(output.Text).Should().Contain("- business/areas/ — Professional projects (300 files)");
        output.Text.Should().EndWith("\n");
        output.StderrLines.Should().BeEmpty();
    }

    [Fact]
    public void Build_IndexFilesOrderedByUpdatedDescending()
    {
        // Arrange
        using var tree = SidedWith(30);

        // Act
        var output = new DigestBuilder(tree.Paths, new DigestOptions(), Clock).Build(DigestSection.Index, null);

        // Assert
        var fileLines = Lines(output.Text).Where(l => l.StartsWith("- business/areas/gen-", StringComparison.Ordinal)).ToList();
        fileLines.Should().HaveCount(30);
        fileLines[0].Should().StartWith("- business/areas/gen-027.md");  // updated 2026-09-28, the newest
        fileLines[1].Should().StartWith("- business/areas/gen-026.md");
        fileLines[^1].Should().StartWith("- business/areas/gen-028.md"); // 2026-09-01, after gen-000 by path
        fileLines[^2].Should().StartWith("- business/areas/gen-000.md");
    }

    [Fact]
    public void Build_IndexSkipsUnderscoreFilesDreamInboxDailyAutoAndIdentity()
    {
        // Arrange
        using var tree = MemoryTree.CopyOf(Path.Combine("digest", "sided"));

        // Act
        var output = new DigestBuilder(tree.Paths, new DigestOptions(), Clock).Build(DigestSection.Index, null);

        // Assert
        output.Text.Should().NotContain("_notes").And.NotContain("_index").And.NotContain(".dream")
            .And.NotContain("inbox/").And.NotContain("daily/").And.NotContain("auto/").And.NotContain("profile.md");
    }

    [Fact]
    public void Build_IdentityOverCap_TruncatesAtLineWithMarkerAsIn27()
    {
        // Arrange
        using var tree = MemoryTree.CopyOf(Path.Combine("digest", "27", "memory-oversize"));

        // Act
        var output = new DigestBuilder(tree.Paths, new DigestOptions(), Clock).Build(DigestSection.Identity, null);

        // Assert
        Bytes(output.Text).Should().BeLessThanOrEqualTo(6000);
        output.Text.Should().EndWith("\n");
        NthLast(output.Text, 1).Should().Be("</zyggy-memory-digest>");
        var marker = IdentityMarker().Match(NthLast(output.Text, 2));
        marker.Success.Should().BeTrue(NthLast(output.Text, 2));
        int.Parse(marker.Groups[1].Value, CultureInfo.InvariantCulture).Should().BeInRange(34001, 34999);
        NthLast(output.Text, 3).Should().StartWith("- [stated] 2026-09-18: profile line ");
        output.StderrLines.Should().ContainSingle().Which.Should().StartWith("zyggy: identity section truncated: profile.md — ");
    }

    [Fact]
    public void Build_DailyOverCap_DropsOldestFilesFirst()
    {
        // Arrange
        using var tree = MemoryTree.CopyOf(Path.Combine("digest", "27", "memory-oversize"));

        // Act
        var output = new DigestBuilder(tree.Paths, new DigestOptions(), Clock).Build(DigestSection.Daily, null);

        // Assert
        Bytes(output.Text).Should().BeLessThanOrEqualTo(8000);
        Lines(output.Text).Where(l => l.StartsWith("## daily/", StringComparison.Ordinal))
            .Should().Equal("## daily/2026-09-29.md", "## daily/2026-09-30.md");
        DailyMarker().IsMatch(NthLast(output.Text, 2)).Should().BeTrue(NthLast(output.Text, 2));
        Lines(output.Text).Count(l => l.Contains("day 30 note", StringComparison.Ordinal)).Should().Be(37);
        Lines(output.Text).Count(l => l.Contains("day 29 note", StringComparison.Ordinal)).Should().BeInRange(1, 36);
        output.StderrLines.Should().ContainSingle();
    }

    [Fact]
    public void Build_CapOverride12000_ClampedTo9500()
    {
        // Arrange
        using var tree = MemoryTree.CopyOf(Path.Combine("digest", "27", "memory-oversize"));
        var options = new DigestOptions { IdentityBytes = 12000 };

        // Act
        var output = new DigestBuilder(tree.Paths, options, Clock).Build(DigestSection.Identity, null);

        // Assert
        Bytes(output.Text).Should().BeLessThanOrEqualTo(9500);
        NthLast(output.Text, 2).Should().EndWith("over cap 9500]");
    }

    [Theory]
    [InlineData("abc", 6000)]
    [InlineData("12k", 6000)]
    [InlineData("-5", 6000)]
    [InlineData("0", 6000)]
    [InlineData("", 6000)]
    [InlineData(null, 6000)]
    [InlineData("3000", 3000)]
    [InlineData("99999", 9500)]
    public void ParseCap_TextValue_FollowsZyCapBytes(string? value, int expected)
    {
        // Act
        var cap = DigestOptions.ParseCap(value, DigestSection.Identity);

        // Assert
        cap.Should().Be(expected);
    }

    [GeneratedRegex(@"^\[index: ([0-9]+) more files not listed — read the category directory\]$")]
    private static partial Regex MoreLine();

    [GeneratedRegex(@"^\[digest truncated: profile\.md — ([0-9]+) bytes over cap 6000\]$")]
    private static partial Regex IdentityMarker();

    [GeneratedRegex(@"^\[digest truncated: daily/2026-09-29\.md — [0-9]+ bytes over cap 8000\]$")]
    private static partial Regex DailyMarker();
}
