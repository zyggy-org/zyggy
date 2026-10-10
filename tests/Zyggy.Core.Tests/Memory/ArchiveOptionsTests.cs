using Zyggy.Core.Memory;

namespace Zyggy.Core.Tests.Memory;

public sealed class ArchiveOptionsTests
{
    public static TheoryData<ArchiveOptions, string> Offending() => new()
    {
        { new ArchiveOptions { ItemMaxBytes = 26_214_401 }, "item_max_bytes" },
        { new ArchiveOptions { ItemMaxBytes = 0 }, "item_max_bytes" },
        { new ArchiveOptions { ProjectMaxBytes = 209_715_201 }, "project_max_bytes" },
        { new ArchiveOptions { ItemMaxBytes = 20_000_000, ProjectMaxBytes = 10_000_000 }, "project_max_bytes" },
        { new ArchiveOptions { TotalMaxBytes = 524_288_001 }, "total_max_bytes" },
        { new ArchiveOptions { ProjectMaxBytes = 100_000_000, TotalMaxBytes = 90_000_000 }, "total_max_bytes" },
        { new ArchiveOptions { AllowedTypes = new HashSet<ArchiveMediaType>() }, "allowed_types" },
        { new ArchiveOptions { SourceDeny = ["relative/folder"] }, "source_deny" },
    };

    [Fact]
    public void Defaults_MatchSpecTable()
    {
        // Act
        var options = new ArchiveOptions();

        // Assert
        options.ItemMaxBytes.Should().Be(10_485_760);
        options.ProjectMaxBytes.Should().Be(52_428_800);
        options.TotalMaxBytes.Should().Be(209_715_200);
        options.AllowedTypes.Should().BeEquivalentTo(Enum.GetValues<ArchiveMediaType>());
        options.SourceDeny.Should().BeEmpty();
        options.Validate().Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Offending))]
    public void Validate_AboveCeilingOrBelowMinimum_NamesKey(ArchiveOptions options, string key)
    {
        // Act
        var offending = options.Validate();

        // Assert
        offending.Should().Contain(key);
    }
}
