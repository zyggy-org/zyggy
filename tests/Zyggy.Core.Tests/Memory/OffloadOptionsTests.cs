using Zyggy.Core.Memory;

namespace Zyggy.Core.Tests.Memory;

/// <summary>Spec 38 Configuration: the <c>offload</c> block's defaults and ranges; an out-of-range value names its key, never clamped.</summary>
public sealed class OffloadOptionsTests
{
    public static TheoryData<string, string> OutOfRange() => new()
    {
        { "root_folder", "" },
        { "root_folder", "a/b/c/d" },
        { "root_folder", "Zyggy/.." },
        { "root_folder", " Zyggy" },
        { "root_folder", "Zyggy\\archive" },
        { "root_folder", "Zy\u0001ggy" },
        { "root_folder", "Zyggy//archive" },
        { "min_bytes", "-1" },
        { "min_bytes", "26214401" },
        { "max_items_per_run", "0" },
        { "max_items_per_run", "201" },
        { "max_bytes_per_run", "1048575" },
        { "max_bytes_per_run", "1073741825" },
        { "max_failures", "0" },
        { "max_failures", "11" },
        { "exclude", "zyggy" },
        { "exclude", "zyggy/Bad Slug" },
    };

    [Fact]
    public void Defaults_MatchSpecTable()
    {
        // Act
        var options = new OffloadOptions();

        // Assert
        options.Enabled.Should().BeFalse();
        options.RootFolder.Should().Be("Zyggy/archive");
        options.MinBytes.Should().Be(0);
        options.MaxItemsPerRun.Should().Be(20);
        options.MaxBytesPerRun.Should().Be(200L * 1024 * 1024);
        options.MaxFailures.Should().Be(3);
        options.Exclude.Should().BeEmpty();
        options.Validate().Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(OutOfRange))]
    public void Validate_OutOfRange_NamesKey(string key, string value)
    {
        // Arrange
        var options = key switch
        {
            "root_folder" => new OffloadOptions { RootFolder = value },
            "min_bytes" => new OffloadOptions { MinBytes = long.Parse(value, System.Globalization.CultureInfo.InvariantCulture) },
            "max_items_per_run" => new OffloadOptions { MaxItemsPerRun = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) },
            "max_bytes_per_run" => new OffloadOptions { MaxBytesPerRun = long.Parse(value, System.Globalization.CultureInfo.InvariantCulture) },
            "max_failures" => new OffloadOptions { MaxFailures = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) },
            _ => new OffloadOptions { Exclude = [value] },
        };

        // Act
        var offending = options.Validate();

        // Assert
        offending.Should().Equal("offload." + key);
    }

    [Theory]
    [InlineData("Zyggy")]
    [InlineData("Zyggy/archive")]
    [InlineData("Documents/Zyggy/archive")]
    [InlineData("My Files/Zyggy")]
    public void Validate_RootFolderUpToThreeSegments_Valid(string root)
    {
        // Act / Assert
        new OffloadOptions { RootFolder = root }.Validate().Should().BeEmpty();
    }

    [Fact]
    public void ArchiveOptions_Validate_IncludesOffloadKeys()
    {
        // Act
        var offending = new ArchiveOptions { Offload = new OffloadOptions { MaxFailures = 0 } }.Validate();

        // Assert
        offending.Should().Equal("offload.max_failures");
    }
}
