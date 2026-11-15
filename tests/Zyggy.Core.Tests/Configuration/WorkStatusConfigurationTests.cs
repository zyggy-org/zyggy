using Zyggy.Core.Configuration;

namespace Zyggy.Core.Tests.Configuration;

// AC-3 (the default rule) and the `supervision` keys this slice reads from node.json: enabled, statusMinutes, detail.
public sealed class WorkStatusConfigurationTests : IDisposable
{
    private const string Minimal = """
        {
          "tenant": "acme",
          "machine": "home-laptop",
          "signWith": "acme/1",
          {extra}
          "bus": {
            "owner": "acme-org",
            "repo": "acme-bus",
            "branch": "main",
            "localPath": "{root}",
            "remote": "https://example.invalid/acme-org/acme-bus.git"
          },
          "poll": { "activeSeconds": 10, "idleSeconds": 60 },
          "secrets": { "root": null }
        }
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zyggy-unit", Guid.NewGuid().ToString("N"));

    public WorkStatusConfigurationTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Read_SupervisionAbsent_EnabledEveryTenMinutesFullDetail()
    {
        // Act
        NodeConfigurationResult result = Read(string.Empty);

        // Assert
        result.IsOk.Should().BeTrue(result.ToString());
        result.Configuration!.Supervision.Should().Be(new SupervisionSettings(true, 10, SupervisionDetail.Full));
    }

    [Fact]
    public void Read_SupervisionAbsent_DiscoveryDetailNames_DetailIsFixed()
    {
        // Act
        NodeConfigurationResult result = Read("\"discovery\": { \"detail\": \"names\" },");

        // Assert
        result.IsOk.Should().BeTrue(result.ToString());
        result.Configuration!.Supervision.Detail.Should().Be(SupervisionDetail.Fixed);
    }

    [Fact]
    public void Read_SupervisionWithoutDetail_DiscoveryDetailNames_DetailIsFixed()
    {
        // Act
        NodeConfigurationResult result = Read("\"discovery\": { \"detail\": \"names\" }, \"supervision\": { \"statusMinutes\": 5 },");

        // Assert
        result.IsOk.Should().BeTrue(result.ToString());
        result.Configuration!.Supervision.Should().Be(new SupervisionSettings(true, 5, SupervisionDetail.Fixed));
    }

    [Fact]
    public void Read_SupervisionDetailFullStated_DiscoveryDetailNames_StaysFull()
    {
        // Act
        NodeConfigurationResult result = Read("\"discovery\": { \"detail\": \"names\" }, \"supervision\": { \"detail\": \"full\" },");

        // Assert
        result.IsOk.Should().BeTrue(result.ToString());
        result.Configuration!.Supervision.Detail.Should().Be(SupervisionDetail.Full);
    }

    [Fact]
    public void Read_SupervisionSection_Typed()
    {
        // Act
        NodeConfigurationResult result = Read("\"supervision\": { \"enabled\": false, \"statusMinutes\": 120, \"detail\": \"fixed\" },");

        // Assert
        result.IsOk.Should().BeTrue(result.ToString());
        result.Configuration!.Supervision.Should().Be(new SupervisionSettings(false, 120, SupervisionDetail.Fixed));
    }

    [Theory]
    [InlineData("5", "supervision", "supervision: must be an object")]
    [InlineData("{ \"statusMinutes\": 0 }", "supervision.statusMinutes", "supervision.statusMinutes: must be an integer from 1 to 120")]
    [InlineData("{ \"statusMinutes\": 121 }", "supervision.statusMinutes", "supervision.statusMinutes: must be an integer from 1 to 120")]
    [InlineData("{ \"detail\": \"names\" }", "supervision.detail", "supervision.detail: must be full or fixed")]
    [InlineData("{ \"enabled\": \"yes\" }", "supervision.enabled", "supervision.enabled: must be true or false")]
    public void Read_SupervisionInvalid_FieldNamed(string section, string field, string message)
    {
        // Act
        NodeConfigurationResult result = Read("\"supervision\": " + section + ",");

        // Assert
        result.Error.Should().Be(NodeConfigurationError.InvalidField);
        result.Field.Should().Be(field);
        result.ToString().Should().Be(message);
    }

    [Fact]
    public void Read_SupervisionWorkMachines_Typed_AbsentIsEmpty()
    {
        // Act
        NodeConfigurationResult listed = Read("\"supervision\": { \"workMachines\": [\"work-laptop\", \"other-laptop\"] },");
        NodeConfigurationResult absent = Read(string.Empty);

        // Assert
        listed.IsOk.Should().BeTrue(listed.ToString());
        listed.Configuration!.Supervision.WorkMachines.Select(m => m.Value).Should().Equal("work-laptop", "other-laptop");
        absent.Configuration!.Supervision.WorkMachines.Should().BeEmpty();
    }

    [Theory]
    [InlineData("\"work-laptop\"")]
    [InlineData("[\"Work Laptop\"]")]
    [InlineData("[5]")]
    public void Read_SupervisionWorkMachinesInvalid_FieldNamed(string value)
    {
        // Act
        NodeConfigurationResult result = Read("\"supervision\": { \"workMachines\": " + value + " },");

        // Assert
        result.Error.Should().Be(NodeConfigurationError.InvalidField);
        result.ToString().Should().Be("supervision.workMachines: must be a list of machine names");
    }

    [Fact]
    public void Read_DiscoveryIntervalOutOfRange_StillNamesTheDiscoveryKey()
    {
        // Act: the ranged-integer reader now takes its section name; discovery keeps its own
        NodeConfigurationResult result = Read("\"discovery\": { \"intervalMinutes\": 0 },");

        // Assert
        result.ToString().Should().Be("discovery.intervalMinutes: must be an integer from 1 to 1440");
    }

    private NodeConfigurationResult Read(string extra)
    {
        string path = Path.Combine(_dir, "node.json");
        File.WriteAllText(path, Minimal.Replace("{extra}", extra, StringComparison.Ordinal).Replace("{root}", _dir.Replace('\\', '/'), StringComparison.Ordinal));
        return NodeConfigurationReader.Read(path);
    }
}
