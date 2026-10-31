using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Memory;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Memory;

/// <summary>Spec 13 AC-28: picked-up inbox lines reach <c>get_context</c> only; the <c>SessionStart</c> digest stays inbox-free.</summary>
public sealed class DigestBuilderPickupTests
{
    private static readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Digest_IdentityIndexDaily_NoInboxLineEvenWithBusFiles()
    {
        // Arrange
        using var tree = MemoryTree.CopyOf(Path.Combine("digest", "sided"));
        tree.Write("inbox/bus-context-home-laptop-01K8ZX3P4Q5R6S7T8V9W0XYZAB-2026-09-30.md", "---\nname: bus context x\n---\n- picked-up-context-marker\n");
        tree.Write("inbox/bus-report-home-laptop-01K8ZX2A3B4C5D6E7F8G9H0JKM-01K8ZX4Q5R6S7T8V9W0XYZAC-2026-09-30.md", "---\nname: bus report x\n---\n- picked-up-report-marker\n");
        var builder = new DigestBuilder(tree.Paths, new DigestOptions(), Clock);

        // Act
        string[] texts = [.. Enum.GetValues<DigestSection>().Select(section => builder.Build(section, startDirectory: null).Text)];

        // Assert
        texts.Should().AllSatisfy(text => text.Should().NotContain("picked-up-").And.NotContain("inbox line"));
    }
}
