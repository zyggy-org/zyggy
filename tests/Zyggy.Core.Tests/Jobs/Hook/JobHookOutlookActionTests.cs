using System.Text.Json;

using Zyggy.Core.Jobs.Hook;

namespace Zyggy.Core.Tests.Jobs.Hook;

/// <summary>
/// Spec 58 slice 7, AC-11: the node's job hook refuses every shell command that names an outlook action (send, move), however it is
/// quoted or prefixed, whatever the job's <c>allowed_tools</c> say — so a job cannot send or move work mail even with its environment
/// variables cleared in the command. The read tools stay allowed when the rules allow them.
/// </summary>
public sealed class JobHookOutlookActionTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "zyggy-hook-root", "work");

    [Theory]
    [InlineData("Bash", "zyggy outlook send --draft 1")]
    [InlineData("Bash", "ZYGGY_JOB= ZYGGY_HOOKS= zyggy outlook send --to a@example.test --subject s --body b")]
    [InlineData("Bash", "env -u ZYGGY_JOB zyggy outlook move m1 --to Archive")]
    [InlineData("Bash", "\"zyggy\" 'outlook' \"move\" m1 --to Archive")]
    [InlineData("Bash", "zyggy outlook $'send' --draft 1")]
    [InlineData("Bash", "zy\\ggy out\"look\" $\"move\" m1 --to Archive")]
    [InlineData("PowerShell", "& 'C:/tools/zyggy.exe' outlook send --draft 1")]
    [InlineData("PowerShell", "$env:ZYGGY_JOB=''; zyggy outlook move m1 --to Archive")]
    public void Decide_OutlookActionInAJob_DeniedWhateverTheRules(string tool, string command)
    {
        // Act
        var decision = Decide(tool, command, "Bash", "PowerShell");

        // Assert
        (decision.Allowed, decision.Rule).Should().Be((false, HookDecision.OutlookActionRule));
    }

    [Theory]
    [InlineData("zyggy outlook brief --since 2026-11-20")]
    [InlineData("zyggy outlook drafts")]
    public void Decide_OutlookReadInAJob_AllowedByTheRules(string command)
    {
        // Act
        var decision = Decide("Bash", command, "Bash(zyggy outlook *)");

        // Assert
        decision.Allowed.Should().BeTrue();
    }

    private static HookDecision Decide(string tool, string command, params string[] rules)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { command }));
        return new JobHookPolicy(Root, rules, ignoreCase: true).Decide(tool, document.RootElement, Root);
    }
}
