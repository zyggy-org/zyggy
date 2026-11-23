using System.Text.Json;

using Zyggy.Core.Jobs.Hook;

namespace Zyggy.Core.Tests.Jobs.Hook;

/// <summary>
/// Spec 58 slice 7, AC-11 (narrowed by decision B to work machines, <c>--work-mail</c>): the node's job hook refuses every shell command that names an outlook action (send, move), however it is
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

    // Verify 01M81R40G1YGA46142HYTV5H7T point 4: an escaped, variable or pieced-together name of the program, the tool or the verb.
    [Theory]
    [InlineData("Bash", "zyggy outlook $'\\x73end' --draft 1")]
    [InlineData("Bash", "zyggy outlook $'\\163end' --draft 1")]
    [InlineData("Bash", "zyggy $'\\x6futlook' send --draft 1")]
    [InlineData("Bash", "zyggy $'\\u006futlook' move m1 --to Archive")]
    [InlineData("Bash", "c=zyggy; $c outlook send --draft 1")]
    [InlineData("Bash", "a=outlook; zyggy $a send --draft 1")]
    [InlineData("Bash", "v=send; zyggy outlook $v --draft 1")]
    [InlineData("Bash", "z=zyg; ${z}gy outlook send --draft 1")]
    [InlineData("Bash", "zyggy out''look se''nd --draft 1")]
    [InlineData("Bash", "zyggy outlook \"$(printf '\\163end')\" --draft 1")]
    [InlineData("Bash", "zyggy outlook `echo send` --draft 1")]
    [InlineData("Bash", "zyggy \"$(echo b3V0bG9vaw== | base64 -d)\" send --draft 1")]
    [InlineData("Bash", "zyggy outlook SEND --draft 1")]
    [InlineData("Bash", "zyggy outlook\tsend --draft 1")]
    [InlineData("Bash", "zyggy outlook s?nd --draft 1")]
    [InlineData("Bash", "zyggy outlook m*e m1 --to Archive")]
    [InlineData("PowerShell", "& ('zyg'+'gy') outlook send --draft 1")]
    [InlineData("PowerShell", "zyggy outlook ([char]0x73+'end') --draft 1")]
    [InlineData("PowerShell", "$v='move'; zyggy outlook $v m1 --to Archive")]
    [InlineData("PowerShell", "zyggy outlook \"s`u{65}nd\" --draft 1")]
    public void Decide_DisguisedOutlookActionInAJob_Denied(string tool, string command)
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

    [Theory]
    [InlineData("dotnet test --filter OutlookActionVerbTests")]
    [InlineData("dotnet build Zyggy.slnx")]
    public void Decide_OutlookNamedWithoutAWriteVerb_AllowedByTheRules(string command)
    {
        // Act
        var decision = Decide("Bash", command, "Bash(dotnet *)");

        // Assert
        decision.Allowed.Should().BeTrue();
    }

    // Decision B, task point 3: on a machine without outlook.enabled the hook does not judge outlook actions, so a job there may name
    // outlook with send or move in a test filter or a commit message; zyggy itself still refuses every send and move from any job.
    [Theory]
    [InlineData("dotnet test --filter \"FullyQualifiedName~Outlook&DisplayName~send\"")]
    [InlineData("git commit -m \"Spec 58: zyggy outlook send and move refused in jobs\"")]
    public void Decide_OutlookActionNamedOnAnotherMachine_AllowedByTheRules(string command)
    {
        // Act
        var decision = DecideOn(workMail: false, "Bash", command, "Bash(dotnet *)", "Bash(git commit *)");

        // Assert
        decision.Allowed.Should().BeTrue();
    }

    // The same commands stay refused on a work machine: there the refusal is complete.
    [Theory]
    [InlineData("dotnet test --filter \"FullyQualifiedName~Outlook&DisplayName~send\"")]
    [InlineData("git commit -m \"Spec 58: zyggy outlook send and move refused in jobs\"")]
    public void Decide_OutlookActionNamedOnAWorkMachine_Denied(string command)
    {
        // Act
        var decision = Decide("Bash", command, "Bash(dotnet *)", "Bash(git commit *)");

        // Assert
        (decision.Allowed, decision.Rule).Should().Be((false, HookDecision.OutlookActionRule));
    }

    // Owner correction 2026-11-23 (AC-B2): on a work machine a job that is not a mail job (its rules name no zyggy outlook) cannot
    // reach work mail: any call to zyggy outlook is refused, however generic the rules that would match it.
    [Theory]
    [InlineData("Bash", "zyggy outlook list", "Read", "Edit", "Write", "Bash(src/*)", "Bash(dotnet *)")]
    [InlineData("Bash", "zyggy outlook brief --since 2026-11-20", "Bash")]
    [InlineData("Bash", "C:/tools/zyggy.exe outlook read m1", "Bash(*)")]
    [InlineData("Bash", "\"zyggy\" 'outlook' list", "Bash")]
    [InlineData("Bash", "env -u ZYGGY_JOB zyggy outlook list", "Bash")]
    [InlineData("Bash", "ZYGGY.EXE Outlook list", "Bash")]
    [InlineData("PowerShell", "& 'C:/tools/zyggy.exe' outlook count", "PowerShell")]
    [InlineData("Bash", "zyggy outlook list", "Bash(zyggy *)")]
    public void Decide_OutlookReadInACodeJob_DeniedOutlookOutsideAMailJob(string tool, string command, params string[] rules)
    {
        // Act
        var decision = Decide(tool, command, rules);

        // Assert
        (decision.Allowed, decision.Rule).Should().Be((false, HookDecision.OutlookReadRule));
    }

    // AC-B2: the code job's ordinary commands, a test filter on the outlook tests included, still run on the work machine.
    [Theory]
    [InlineData("dotnet build Zyggy.slnx")]
    [InlineData("dotnet test --filter Zyggy.Core.Tests.Outlook")]
    [InlineData("src/run.sh")]
    public void Decide_CodeJobCommandOnAWorkMachine_Allowed(string command)
    {
        // Act
        var decision = Decide("Bash", command, "Read", "Edit", "Write", "Bash(src/*)", "Bash(dotnet *)");

        // Assert
        decision.Allowed.Should().BeTrue();
    }

    private static HookDecision Decide(string tool, string command, params string[] rules) => DecideOn(workMail: true, tool, command, rules);

    private static HookDecision DecideOn(bool workMail, string tool, string command, params string[] rules)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { command }));
        return new JobHookPolicy(Root, rules, ignoreCase: true, workMail: workMail).Decide(tool, document.RootElement, Root);
    }
}
