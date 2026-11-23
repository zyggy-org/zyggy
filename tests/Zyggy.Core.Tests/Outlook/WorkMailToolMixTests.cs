using Zyggy.Core.Outlook;

namespace Zyggy.Core.Tests.Outlook;

/// <summary>
/// Spec 58 decision B as corrected by the owner (2026-11-23): enabling Outlook on a work machine changes nothing for other work. A job is
/// a <b>mail job</b> only when its <c>allowed_tools</c> explicitly grant the outlook verbs (a shell rule naming <c>zyggy outlook</c>);
/// generic rules (<c>Bash(src/*)</c>, <c>Bash(dotnet *)</c>, bare <c>Bash</c>, <c>Edit</c>, <c>Write</c>, an MCP server) never make one. A
/// mail job may hold only <c>Read</c>, <c>Grep</c>, <c>Glob</c> and the <c>zyggy outlook</c> read verbs; any other job keeps its tools.
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
    public void Refusal_MailJobWithReadOnlyRules_Null(params string[] rules)
    {
        // Act
        string? refusal = WorkMailToolMix.Refusal(rules);

        // Assert
        refusal.Should().BeNull();
    }

    // AC-B1 (owner correction 2026-11-23): a code job is accepted exactly as before Outlook was enabled, whatever its generic rules.
    [Theory]
    [InlineData("Read", "Grep", "Glob")]
    [InlineData("Read", "Edit", "Write", "Bash(src/*)", "Bash(dotnet *)")]
    [InlineData("Read", "Edit", "Write", "Bash(dotnet *)", "Bash(git status*)", "Bash(git commit *)")]
    [InlineData("Bash(node *)", "Bash(python *)", "Write")]
    [InlineData("Bash")]
    [InlineData("PowerShell")]
    [InlineData("Bash(*)")]
    [InlineData("Bash(zyggy *)")]
    [InlineData("Read,Bash")]
    [InlineData("WebFetch", "Task")]
    [InlineData("mcp__github", "Bash(dotnet *)")]
    [InlineData("Bash(dotnet test --filter \"FullyQualifiedName~Zyggy.Core.Tests.Outlook\")")]
    public void Refusal_CodeJobOnAWorkMachine_Null(params string[] rules)
    {
        // Act
        string? refusal = WorkMailToolMix.Refusal(rules);

        // Assert
        refusal.Should().BeNull();
    }

    // AC-B1: a mail job that holds anything beside the read verbs and the file reads is still refused.
    [Theory]
    [InlineData("Bash(zyggy outlook *)", "Bash")]
    [InlineData("Bash(zyggy outlook *)", "Edit")]
    [InlineData("Bash(zyggy outlook *)", "Bash(python *)")]
    [InlineData("Bash(zyggy outlook *)", "Bash(dotnet *)")]
    [InlineData("Bash(zyggy outlook *)", "Bash(src/*)")]
    [InlineData("Bash(zyggy outlook *)", "Write")]
    [InlineData("Bash(zyggy outlook *)", "Edit(**)")]
    [InlineData("Bash(zyggy outlook *)", "PowerShell(Get-ChildItem)")]
    [InlineData("Bash(zyggy outlook *)", "Task")]
    [InlineData("Bash(zyggy outlook *)", "Agent")]
    [InlineData("Bash(zyggy outlook *)", "mcp__outlook")]
    [InlineData("Bash(zyggy outlook *)", "WebFetch")]
    [InlineData("Bash(zyggy outlook *,python *)")]
    [InlineData("Bash(zyggy outlook *),Bash(python *)")]
    [InlineData("Bash(C:/tools/zyggy.exe outlook list)")]
    [InlineData("Bash(ZYGGY_JOB= zyggy outlook *)")]
    [InlineData("Bash(zyggy outlook send *)")]
    [InlineData("Bash(zyggy outlook move:*)")]
    [InlineData("Bash(zyggy outlook list *)", "Bash(zyggy run *)")]
    [InlineData("PowerShell(zyggy outlook list *)")]
    [InlineData("Bash(zyggy outlook list; python x.py)")]
    [InlineData("Bash(zyggy outlook list $(python x.py))")]
    [InlineData("Bash(zyggy outlook l*)", "Bash(zyggy outlook mo*)")]
    public void Refusal_MailJobWithAnythingButTheReadVerbs_Refused(params string[] rules)
    {
        // Act
        string? refusal = WorkMailToolMix.Refusal(rules);

        // Assert
        refusal.Should().NotBeNull();
    }

    [Theory]
    [InlineData(true, "Bash(zyggy outlook *)")]
    [InlineData(true, "Read", "bash(Zyggy.exe Outlook list *)")]
    [InlineData(true, "Bash(ZYGGY_JOB= zyggy outlook *)")]
    [InlineData(true, "Read,Bash(zyggy outlook brief)")]
    [InlineData(false, "Read", "Edit", "Write", "Bash(src/*)", "Bash(dotnet *)")]
    [InlineData(false, "Bash")]
    [InlineData(false, "Bash(zyggy *)")]
    [InlineData(false, "mcp__outlook")]
    [InlineData(false, "Bash(dotnet test --filter Zyggy.Core.Tests.Outlook)")]
    public void IsMailJob_OnlyAnExplicitOutlookRule_MakesAMailJob(bool expected, params string[] rules)
    {
        // Act
        bool mailJob = WorkMailToolMix.IsMailJob(rules);

        // Assert
        mailJob.Should().Be(expected);
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
