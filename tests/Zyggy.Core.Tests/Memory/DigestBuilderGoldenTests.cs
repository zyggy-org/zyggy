using System.Text;

using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Memory;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Memory;

/// <summary>
/// AC-28: <c>identity</c> and <c>daily</c> are byte-identical to 27's bats oracles (copied from zyggy-core e0b8290, never
/// produced here); <c>index</c> equals a hand-derived golden over a hand-written two-sided fixture.
/// </summary>
public sealed class DigestBuilderGoldenTests
{
    private static readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero));

    private static string Expected(params string[] parts) =>
        Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine([Golden.Directory, "digest", .. parts])));

    private static DigestBuilder Builder(MemoryTree tree) => new(tree.Paths, new DigestOptions(), Clock);

    [Fact]
    public void Build_Identity_On27Fixture_IsByteIdenticalToGolden()
    {
        // Arrange
        using var tree = MemoryTree.CopyOf(Path.Combine("digest", "27", "memory"));

        // Act
        var output = Builder(tree).Build(DigestSection.Identity, startDirectory: null);

        // Assert
        output.Text.Should().Be(Expected("27", "expected", "digest-identity.txt"));
        output.StderrLines.Should().BeEmpty();
    }

    [Fact]
    public void Build_Daily_On27Fixture_IsByteIdenticalToGolden()
    {
        // Arrange
        using var tree = MemoryTree.CopyOf(Path.Combine("digest", "27", "memory"));

        // Act
        var output = Builder(tree).Build(DigestSection.Daily, startDirectory: null);

        // Assert
        output.Text.Should().Be(Expected("27", "expected", "digest-daily.txt"));
        output.StderrLines.Should().BeEmpty();
    }

    [Fact]
    public void Build_Index_OnSidedFixture_EqualsHandDerivedGolden()
    {
        // Arrange
        using var tree = MemoryTree.CopyOf(Path.Combine("digest", "sided"));

        // Act
        var output = Builder(tree).Build(DigestSection.Index, startDirectory: null);

        // Assert
        output.Text.Should().Be(Expected("sided", "expected-index.txt"));
        output.StderrLines.Should().BeEmpty();
    }

    // Spec 37 AC-23: archive items and sidecars change no digest section.
    private static void AddArchiveFiles(MemoryTree tree)
    {
        tree.Write("archive/zyggy/quote-2026.txt", "Quote for the Zyggy roof repair.\nTotal 1 234,00 EUR, valid 30 days.\n");
        tree.Write("archive/zyggy/quote-2026.md", Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(Golden.Directory, "archive", "sidecar-text.md"))));
    }

    [Fact]
    public void Build_Identity_WithArchiveFiles_IsByteIdenticalToWithout()
    {
        // Arrange
        using var tree = MemoryTree.CopyOf(Path.Combine("digest", "27", "memory"));
        AddArchiveFiles(tree);

        // Act
        var output = Builder(tree).Build(DigestSection.Identity, startDirectory: null);

        // Assert
        output.Text.Should().Be(Expected("27", "expected", "digest-identity.txt"));
    }

    [Fact]
    public void Build_Index_WithArchiveFiles_EqualsHandDerivedGolden()
    {
        // Arrange
        using var tree = MemoryTree.CopyOf(Path.Combine("digest", "sided"));
        AddArchiveFiles(tree);

        // Act
        var output = Builder(tree).Build(DigestSection.Index, startDirectory: null);

        // Assert
        output.Text.Should().Be(Expected("sided", "expected-index.txt"));
        output.StderrLines.Should().BeEmpty();
    }

    [Fact]
    public void Build_Daily_WithArchiveFiles_IsByteIdenticalToWithout()
    {
        // Arrange
        using var tree = MemoryTree.CopyOf(Path.Combine("digest", "27", "memory"));
        AddArchiveFiles(tree);

        // Act
        var output = Builder(tree).Build(DigestSection.Daily, startDirectory: null);

        // Assert
        output.Text.Should().Be(Expected("27", "expected", "digest-daily.txt"));
    }

    [Fact]
    public void Build_Identity_ClaudeMdAboveStartDirectory_AddsWarningLineAndStderr()
    {
        // Arrange
        using var tree = MemoryTree.CopyOf(Path.Combine("digest", "27", "memory"));
        var project = Path.Combine(tree.Root, "project");
        var start = Path.Combine(project, "sub", "dir");
        Directory.CreateDirectory(start);
        File.WriteAllText(Path.Combine(project, "CLAUDE.md"), "# instructions\n");
        var found = Path.Join(project, "CLAUDE.md");
        var warning = $"[warning] CLAUDE.md found at {found}: AGENTS.md may not be loaded — see runbook";

        // Act
        var output = Builder(tree).Build(DigestSection.Identity, start);

        // Assert
        output.Text.Split('\n')[1].Should().Be(warning);
        output.StderrLines.Should().Equal(warning);
    }
}
