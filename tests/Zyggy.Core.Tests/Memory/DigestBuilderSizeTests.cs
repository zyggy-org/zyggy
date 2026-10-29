using System.Text;

using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Memory;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Memory;

/// <summary>
/// Spec 11 AC-22: the three session-start sections built with the default <see cref="DigestOptions"/> stay under the §7
/// "&lt; 6k tokens" target together, with the Hub's own estimate of <see cref="ContextOptions.BytesPerToken"/> bytes per token.
/// </summary>
public sealed class DigestBuilderSizeTests
{
    private const int MaxBytes = 20000;
    private const int MaxTokensExclusive = 6000;

    private static readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero));

    private static int ThreeSectionBytes(params string[] fixture)
    {
        using var tree = MemoryTree.CopyOf(Path.Combine(["digest", .. fixture]));
        var builder = new DigestBuilder(tree.Paths, new DigestOptions(), Clock);
        return new[] { DigestSection.Identity, DigestSection.Index, DigestSection.Daily }
            .Sum(section => Encoding.UTF8.GetByteCount(builder.Build(section, startDirectory: null).Text));
    }

    [Fact]
    public void Build_ThreeSections_On27Fixture_SumAtMost20000BytesAndUnder6000Tokens()
    {
        // Act
        var bytes = ThreeSectionBytes("27", "memory");

        // Assert
        bytes.Should().BeLessThanOrEqualTo(MaxBytes);
        (bytes / new ContextOptions().BytesPerToken).Should().BeLessThan(MaxTokensExclusive);
    }

    [Fact]
    public void Build_ThreeSections_OnSidedFixture_SumAtMost20000BytesAndUnder6000Tokens()
    {
        // Act
        var bytes = ThreeSectionBytes("sided");

        // Assert
        bytes.Should().BeLessThanOrEqualTo(MaxBytes);
        (bytes / new ContextOptions().BytesPerToken).Should().BeLessThan(MaxTokensExclusive);
    }

    [Fact]
    public void Build_ThreeSections_OversizeFixture_StillCappedUnder20000()
    {
        // Act: every section truncates here; the stderr lines are allowed
        var bytes = ThreeSectionBytes("27", "memory-oversize");

        // Assert
        bytes.Should().BeLessThanOrEqualTo(MaxBytes);
        (bytes / new ContextOptions().BytesPerToken).Should().BeLessThan(MaxTokensExclusive);
    }
}
