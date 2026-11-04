using Zyggy.Core.Envelope;
using Zyggy.Core.Setup;

namespace Zyggy.Core.Tests.Setup;

/// <summary>Spec 42 CLI surface, plan 42 A1: <c>setup: &lt;item words&gt;: &lt;outcome words&gt;[ - &lt;detail&gt;]</c>.</summary>
public sealed class SetupLineTests
{
    [Theory]
    [InlineData(SetupItem.NodeJson, SetupOutcome.AlreadyDone, null, "setup: node.json: already done")]
    [InlineData(SetupItem.NodeJson, SetupOutcome.Done, "added claude.path (backup b)", "setup: node.json: done - added claude.path (backup b)")]
    [InlineData(SetupItem.ZyggyCore, SetupOutcome.LeftAsIs, "dirty working copy", "setup: zyggy-core: left as is - dirty working copy")]
    [InlineData(SetupItem.SettingsEnv, SetupOutcome.AlreadyDone, null, "setup: settings: already done")]
    [InlineData(SetupItem.SettingsDeny, SetupOutcome.Done, "permissions.deny", "setup: settings: done - permissions.deny")]
    [InlineData(SetupItem.SkillRemember, SetupOutcome.Done, null, "setup: skill remember: done")]
    [InlineData(SetupItem.SkillArchive, SetupOutcome.Failed, "zyggy-core not available", "setup: skill archive: failed - zyggy-core not available")]
    [InlineData(SetupItem.SecretSigningKey, SetupOutcome.Missing, "run: zyggy secret set hmac/1 --hex", "setup: signing key acme/1: missing - run: zyggy secret set hmac/1 --hex")]
    [InlineData(SetupItem.SecretGitHubPat, SetupOutcome.Missing, "run: zyggy secret set github-pat", "setup: github-pat: missing - run: zyggy secret set github-pat")]
    [InlineData(SetupItem.SpikeFileStore, SetupOutcome.LeftAsIs, "x", "setup: spike file store: left as is - x")]
    [InlineData(SetupItem.NodeJson, SetupOutcome.Failed, "", "setup: node.json: failed")]
    public void Render_EveryItemAndOutcome_Grammar(SetupItem item, SetupOutcome outcome, string? detail, string expected)
    {
        // Arrange
        var line = new SetupLine(item, outcome, detail);

        // Act
        string rendered = line.Render(KeyId.Parse("acme/1"));

        // Assert
        rendered.Should().Be(expected);
    }

    [Fact]
    public void Render_EveryItemAndOutcome_HasWords()
    {
        // Act
        var rendered = Enum.GetValues<SetupItem>().SelectMany(i => Enum.GetValues<SetupOutcome>().Select(o => new SetupLine(i, o, null).Render(KeyId.Parse("acme/1")))).ToList();

        // Assert
        rendered.Should().OnlyContain(r => r.StartsWith("setup: ", StringComparison.Ordinal) && !r.Contains("  ", StringComparison.Ordinal) && !r.EndsWith(' '));
    }
}
