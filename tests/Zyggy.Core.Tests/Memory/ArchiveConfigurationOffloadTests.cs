using Zyggy.Core.Memory;

namespace Zyggy.Core.Tests.Memory;

/// <summary>Spec 38 AC-3 (configuration): <c>archive.json</c>'s <c>offload</c> block — off without it, applied with it, a bad nested key named.</summary>
public sealed class ArchiveConfigurationOffloadTests
{
    private const string File = "/instance/archive.json";

    public static TheoryData<string, string> Invalid() => new()
    {
        { """{"offload": {"foo": 1}}""", "offload.foo is not an archive.json key (archive.json)" },
        { """{"offload": {"max_failures": "3"}}""", "offload.max_failures has the wrong type (archive.json)" },
        { """{"offload": {"enabled": "yes"}}""", "offload.enabled has the wrong type (archive.json)" },
        { """{"offload": {"exclude": "zyggy/quote"}}""", "offload.exclude has the wrong type (archive.json)" },
        { """{"offload": {"max_items_per_run": 201}}""", "offload.max_items_per_run is above its ceiling or below its minimum (archive.json)" },
        { """{"offload": []}""", "offload has the wrong type (archive.json)" },
    };

    [Fact]
    public void Load_NoOffloadBlock_DisabledDefaults()
    {
        // Act
        var result = Load("""{"item_max_bytes": 1000}""");

        // Assert
        result.Error.Should().BeNull();
        result.Options!.Offload.Should().BeEquivalentTo(new OffloadOptions());
        result.Options.Offload.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Load_OffloadBlock_Applied()
    {
        // Act
        var result = Load("""
            {"offload": {"enabled": true, "root_folder": "Documents/Zyggy", "min_bytes": 10, "max_items_per_run": 1,
              "max_bytes_per_run": 2097152, "max_failures": 5, "exclude": ["zyggy/quote-2026"]}}
            """);

        // Assert
        result.Error.Should().BeNull();
        result.Options!.Offload.Should().BeEquivalentTo(new OffloadOptions
        {
            Enabled = true,
            RootFolder = "Documents/Zyggy",
            MinBytes = 10,
            MaxItemsPerRun = 1,
            MaxBytesPerRun = 2_097_152,
            MaxFailures = 5,
            Exclude = ["zyggy/quote-2026"],
        });
    }

    [Theory]
    [MemberData(nameof(Invalid))]
    public void Load_OffloadInvalid_NamesNestedKey(string json, string error)
    {
        // Act
        var result = Load(json);

        // Assert
        result.Options.Should().BeNull();
        result.Error.Should().Be(error);
    }

    [Fact]
    public void Load_37KeysWithOffload_Unchanged()
    {
        // Act
        var result = Load("""{"item_max_bytes": 1000, "project_max_bytes": 2000, "offload": {"enabled": true}}""");

        // Assert
        result.Options!.ItemMaxBytes.Should().Be(1000);
        result.Options.ProjectMaxBytes.Should().Be(2000);
        result.Options.TotalMaxBytes.Should().Be(new ArchiveOptions().TotalMaxBytes);
        result.Options.Offload.Enabled.Should().BeTrue();
    }

    private static ArchiveConfigurationResult Load(string json) =>
        ArchiveConfiguration.Load(new Dictionary<string, string?> { ["ZYGGY_ARCHIVE_CONFIG"] = File }, path => path == File ? json : null);
}
