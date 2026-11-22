using Zyggy.Core.Outlook;

namespace Zyggy.Core.Tests.Outlook;

/// <summary>
/// Spec 58 decision B (2026-11-22, verify 01M81R40G1YGA46142HYTV5H7T point 5): on a work machine a job whose <c>allowed_tools</c> can both
/// read work mail and run code is refused at intake. A job that can read work mail (a rule that can run a <c>zyggy outlook</c> verb, or a
/// bare shell rule) may hold only <c>Read</c>, <c>Grep</c>, <c>Glob</c> and <c>Bash</c> rules for the <c>zyggy outlook</c> read verbs.
/// </summary>
public sealed class WorkMailToolMixTests
{
    // AC-B1: the mail-reading job of Central's timer, the read verbs one by one, and file reads beside them.
    [Theory]
    [InlineData("Bash(zyggy outlook *)")]
    [InlineData("Bash(zyggy outlook brief *)", "Bash(zyggy outlook brief-done *)", "Read", "Grep", "Glob")]
    [InlineData("Bash(zyggy outlook policy)", "Bash(zyggy outlook count *)", "Bash(zyggy outlook list *)", "Bash(zyggy outlook read *)")]
    [InlineData("Bash(zyggy outlook share *)", "Bash(zyggy outlook drafts)", "Read(notes/**)")]
    [InlineData("Bash(zyggy outlook list:*)")]
    [InlineData("Bash(zyggy.exe outlook brief --since *)")]
    public void Refusal_MailReadingJobWithReadOnlyRules_Null(params string[] rules)
    {
        // Act
        string? refusal = WorkMailToolMix.Refusal(rules);

        // Assert
        refusal.Should().BeNull();
    }

    // AC-B1: a job that does not read work mail keeps its tools, code runners included (an accepted risk, decision B).
    [Theory]
    [InlineData("Read", "Grep", "Glob")]
    [InlineData("Read", "Edit", "Write", "Bash(dotnet *)", "Bash(git status*)", "Bash(git commit *)")]
    [InlineData("Bash(python *)", "Write")]
    [InlineData("WebFetch", "mcp__github", "Task")]
    [InlineData("Bash(dotnet test --filter \"FullyQualifiedName~OutlookAction\")")]
    public void Refusal_JobThatReadsNoWorkMail_Null(params string[] rules)
    {
        // Act
        string? refusal = WorkMailToolMix.Refusal(rules);

        // Assert
        refusal.Should().BeNull();
    }

    // AC-B1: every way a job can both read work mail and run code, or hold anything beside the read verbs, is refused.
    [Theory]
    [InlineData("Bash")]
    [InlineData("PowerShell")]
    [InlineData("Bash(*)")]
    [InlineData("Bash()")]
    [InlineData("bash")]
    [InlineData("Bash ")]
    [InlineData("Bash(zyggy outlook *)", "Bash(python *)")]
    [InlineData("Bash(zyggy outlook *)", "Bash(dotnet *)")]
    [InlineData("Bash(zyggy outlook *)", "Bash(node *)")]
    [InlineData("Bash(zyggy outlook *)", "Bash(./run.sh)")]
    [InlineData("Bash(zyggy outlook *)", "Write")]
    [InlineData("Bash(zyggy outlook *)", "Edit(**)")]
    [InlineData("Bash(zyggy outlook *)", "PowerShell(Get-ChildItem)")]
    [InlineData("Bash(zyggy outlook *)", "Task")]
    [InlineData("Bash(zyggy outlook *)", "Agent")]
    [InlineData("Bash(zyggy outlook *)", "mcp__outlook")]
    [InlineData("Bash(zyggy outlook *)", "WebFetch")]
    [InlineData("Bash(zyggy outlook *)", "NotebookEdit")]
    [InlineData("Bash(zyggy outlook *,python *)")]
    [InlineData("Bash(zyggy outlook *),Bash(python *)")]
    [InlineData("Read,Bash")]
    [InlineData("Bash(zyggy *)")]
    [InlineData("Bash(zy*)")]
    [InlineData("Bash(*outlook*)")]
    [InlineData("Bash(C:/tools/zyggy.exe outlook list)")]
    [InlineData("Bash(ZYGGY_JOB= zyggy outlook *)")]
    [InlineData("Bash(zyggy outlook send *)")]
    [InlineData("Bash(zyggy outlook move:*)")]
    [InlineData("Bash(zyggy outlook list *)", "Bash(zyggy run *)")]
    [InlineData("PowerShell(zyggy outlook list *)")]
    [InlineData("Bash(zyggy outlook list; python x.py)")]
    [InlineData("Bash(zyggy outlook list $(python x.py))")]
    [InlineData("Bash(zyggy outlook l*)", "Bash(zyggy outlook mo*)")]
    public void Refusal_WorkMailWithAnythingButTheReadVerbs_Refused(params string[] rules)
    {
        // Act
        string? refusal = WorkMailToolMix.Refusal(rules);

        // Assert
        refusal.Should().NotBeNull();
    }

    [Fact]
    public void Refusal_MixedJob_NamesTheMailRuleAndTheCodeRule()
    {
        // Act
        string? refusal = WorkMailToolMix.Refusal(["Read", "Bash(zyggy outlook *)", "Bash(python *)"]);

        // Assert
        refusal.Should().Be("allowed_tools can read work mail (Bash(zyggy outlook *)) and run code (Bash(python *)) on a work machine");
    }
}
