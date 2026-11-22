using System.Text.Json;

using Zyggy.Core.Configuration;
using Zyggy.Core.Outlook;

namespace Zyggy.Core.Tests.Configuration;

/// <summary>
/// Spec 58 slice 2 (Configuration table): the reader keeps the <c>outlook</c> object of <c>node.json</c> for the outlook tools — unknown
/// to the node service, read by the tools through <see cref="OutlookSettings"/> and <see cref="OutlookActionSettings"/>; absent or not an
/// object means no section.
/// </summary>
public sealed class OutlookSectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zyggy-unit", Guid.NewGuid().ToString("N"));

    public OutlookSectionTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Read_OutlookObject_KeptForTheTools()
    {
        // Act
        NodeConfiguration configuration = Read("""
            "outlook": { "enabled": true, "policyConfirmed": { "date": "2026-11-21", "scope": ["read"] }, "read": { "maxItems": 7 }, "actions": ["move"] }
            """);

        // Assert
        var settings = OutlookSettings.FromNodeJson(configuration.Outlook);
        var actions = OutlookActionSettings.FromNodeJson(configuration.Outlook);
        (settings.Enabled, settings.PolicyMirror?.Scope.Single(), settings.ReadMaxItems, actions.Actions.Single()).Should().Be((true, "read", 7, "move"));
    }

    [Fact]
    public void Read_OutlookAbsent_Null()
    {
        // Act
        NodeConfiguration configuration = Read(null);

        // Assert
        configuration.Outlook.Should().BeNull();
    }

    [Theory]
    [InlineData("\"outlook\": true")]
    [InlineData("\"outlook\": null")]
    [InlineData("\"outlook\": [1]")]
    public void Read_OutlookNotAnObject_Null(string section)
    {
        // Act
        NodeConfiguration configuration = Read(section);

        // Assert
        configuration.Outlook.Should().BeNull();
    }

    [Fact]
    public void Read_OutlookObject_SurvivesTheDocumentsDisposal()
    {
        // Act
        NodeConfiguration configuration = Read("\"outlook\": { \"enabled\": true }");
        GC.Collect();

        // Assert
        configuration.Outlook!.Value.GetProperty("enabled").ValueKind.Should().Be(JsonValueKind.True);
    }

    private NodeConfiguration Read(string? section)
    {
        string root = _dir.Replace('\\', '/');
        string line = section is null ? string.Empty : "  " + section + ",\n";
        string json = "{\n  \"tenant\": \"acme\",\n  \"machine\": \"work-laptop\",\n" + line +
            "  \"bus\": { \"owner\": \"acme-org\", \"repo\": \"acme-bus\", \"localPath\": \"" + root + "\", \"remote\": \"https://example.invalid/acme-org/acme-bus.git\" }\n}\n";
        string path = Path.Combine(_dir, "node.json");
        File.WriteAllText(path, json);
        NodeConfigurationResult result = NodeConfigurationReader.Read(path);
        result.Configuration.Should().NotBeNull(result.Detail);
        return result.Configuration!;
    }
}
