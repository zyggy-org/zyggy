using System.Text.Json;

using Zyggy.Core.Outlook;

namespace Zyggy.Core.Tests.Outlook;

// Spec 58 Configuration: outlook.actions (default [] — read only; a subset of send, move), outlook.send.maxRecipients and
// outlook.send.bodyMaxChars (default as m365, tighten only), outlook.move.excludedFolders (default []).
public sealed class OutlookGuardSettingsTests
{
    private static readonly string[] SendMove = ["send", "move"];
    private static readonly string[] PrivateAndHr = ["Private", "Inbox/HR"];

    private static OutlookActionSettings Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return OutlookActionSettings.FromNodeJson(document.RootElement);
    }

    [Fact]
    public void FromNodeJson_NoSection_ReadOnlyWithTheM365Defaults()
    {
        // Act
        var settings = OutlookActionSettings.FromNodeJson(null);

        // Assert
        settings.Should().BeEquivalentTo(new { Actions = Array.Empty<string>(), SendMaxRecipients = 10, SendBodyMaxChars = 4000, MoveExcludedFolders = Array.Empty<string>() });
    }

    [Fact]
    public void FromNodeJson_FullSection_ReadsEveryKey()
    {
        // Act
        var settings = Parse("""{"enabled":true,"actions":["send","move"],"send":{"maxRecipients":3,"bodyMaxChars":2000},"move":{"excludedFolders":["Private","Inbox/HR"]}}""");

        // Assert
        settings.Should().BeEquivalentTo(new { Actions = SendMove, SendMaxRecipients = 3, SendBodyMaxChars = 2000, MoveExcludedFolders = PrivateAndHr });
    }

    [Theory]
    [InlineData("""{"send":{"maxRecipients":50}}""", 10)]
    [InlineData("""{"send":{"maxRecipients":0}}""", 10)]
    [InlineData("""{"send":{"maxRecipients":"3"}}""", 10)]
    [InlineData("""{"send":{"maxRecipients":1}}""", 1)]
    public void FromNodeJson_MaxRecipients_TightenOnly(string json, int expected)
    {
        // Act
        var settings = Parse(json);

        // Assert
        settings.SendMaxRecipients.Should().Be(expected);
    }

    [Theory]
    [InlineData("""{"send":{"bodyMaxChars":100000}}""", 4000)]
    [InlineData("""{"send":{"bodyMaxChars":-1}}""", 4000)]
    [InlineData("""{"send":{"bodyMaxChars":1500}}""", 1500)]
    public void FromNodeJson_BodyMaxChars_TightenOnly(string json, int expected)
    {
        // Act
        var settings = Parse(json);

        // Assert
        settings.SendBodyMaxChars.Should().Be(expected);
    }

    [Theory]
    [InlineData("""{"actions":["send","delete","forward"]}""", new[] { "send" })]
    [InlineData("""{"actions":["SEND","move"]}""", new[] { "move" })]
    [InlineData("""{"actions":"send"}""", new string[0])]
    [InlineData("""{"actions":[1,"move"]}""", new[] { "move" })]
    public void FromNodeJson_Actions_OnlySendAndMove(string json, string[] expected)
    {
        // Act
        var settings = Parse(json);

        // Assert
        settings.Actions.Should().BeEquivalentTo(expected);
    }
}
