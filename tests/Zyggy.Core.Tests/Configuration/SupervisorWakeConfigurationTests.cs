using Zyggy.Core.Configuration;

namespace Zyggy.Core.Tests.Configuration;

// Spec 18 slice 10, Configuration: the `supervision` keys of Central's wake — project and runsPerHour.
public sealed class SupervisorWakeConfigurationTests : IDisposable
{
    private const string Minimal = """
        {
          "tenant": "acme",
          "machine": "central",
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

    public SupervisorWakeConfigurationTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Read_SupervisionAbsent_NoProjectTwentyRunsPerHour()
    {
        // Act
        NodeConfigurationResult result = Read(string.Empty);

        // Assert
        result.IsOk.Should().BeTrue(result.ToString());
        new { result.Configuration!.Supervision.Project, result.Configuration.Supervision.RunsPerHour }
            .Should().BeEquivalentTo(new { Project = (string?)null, RunsPerHour = 20 });
    }

    [Fact]
    public void Read_ProjectAndRunsPerHour_BothRead()
    {
        // Act
        NodeConfigurationResult result = Read("\"supervision\": { \"project\": \"central-desk\", \"runsPerHour\": 120 },");

        // Assert
        result.IsOk.Should().BeTrue(result.ToString());
        new { result.Configuration!.Supervision.Project, result.Configuration.Supervision.RunsPerHour }
            .Should().BeEquivalentTo(new { Project = "central-desk", RunsPerHour = 120 });
    }

    [Theory]
    [InlineData("{ \"runsPerHour\": 0 }", "supervision.runsPerHour: must be an integer from 1 to 120")]
    [InlineData("{ \"runsPerHour\": 121 }", "supervision.runsPerHour: must be an integer from 1 to 120")]
    [InlineData("{ \"project\": 5 }", "supervision.project: must be a project name")]
    [InlineData("{ \"project\": \"\" }", "supervision.project: must be a project name")]
    public void Read_ValueOutOfRange_StartRefused(string section, string message)
    {
        // Act
        NodeConfigurationResult result = Read("\"supervision\": " + section + ",");

        // Assert
        result.ToString().Should().Be(message);
    }

    // Slice 11: the folders a work-machine answer may read.
    [Fact]
    public void Read_OutboundPaths_ReadAsGiven()
    {
        // Arrange
        string outbound = Path.Combine(_dir, "memory", "outbound").Replace('\\', '/');

        // Act
        NodeConfigurationResult result = Read($"\"supervision\": {{ \"workMachines\": [\"work-laptop\"], \"outboundPaths\": [\"{outbound}\"] }},");

        // Assert
        result.IsOk.Should().BeTrue(result.ToString());
        result.Configuration!.Supervision.OutboundPaths.Should().Equal(outbound);
    }

    [Theory]
    [InlineData("\"memory/outbound\"")]
    [InlineData("[\"memory/outbound\"]")]
    [InlineData("[5]")]
    public void Read_OutboundPathsNotAListOfAbsoluteFolders_StartRefused(string value)
    {
        // Act
        NodeConfigurationResult result = Read("\"supervision\": { \"outboundPaths\": " + value + " },");

        // Assert
        result.ToString().Should().Be("supervision.outboundPaths: must be a list of absolute folders");
    }

    // On Central a work machine cannot be answered without a folder to answer from.
    [Fact]
    public void Read_Central_WorkMachinesWithoutOutboundPaths_StartRefused()
    {
        // Act
        NodeConfigurationResult result = Read("\"supervision\": { \"workMachines\": [\"work-laptop\"] },");

        // Assert
        result.ToString().Should().Be("supervision.outboundPaths: must name at least one folder when supervision.workMachines is set");
    }

    // A laptop lists work machines only for its own `zyggy work` view.
    [Fact]
    public void Read_Laptop_WorkMachinesWithoutOutboundPaths_Accepted()
    {
        // Act
        NodeConfigurationResult result = Read("\"supervision\": { \"workMachines\": [\"work-laptop\"] },", machine: "home-laptop");

        // Assert
        result.IsOk.Should().BeTrue(result.ToString());
    }

    private NodeConfigurationResult Read(string extra, string machine = "central")
    {
        string path = Path.Combine(_dir, "node.json");
        extra = "\"machine\": \"" + machine + "\", " + extra;
        File.WriteAllText(path, Minimal.Replace("\"machine\": \"central\",", string.Empty, StringComparison.Ordinal).Replace("{extra}", extra, StringComparison.Ordinal).Replace("{root}", _dir.Replace('\\', '/'), StringComparison.Ordinal));
        return NodeConfigurationReader.Read(path);
    }
}
