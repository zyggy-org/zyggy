using Zyggy.Core.Outlook;

namespace Zyggy.Core.Tests.Outlook;

/// <summary>
/// Spec 58 decision B as corrected by the owner (2026-11-23): enabling Outlook on a work machine changes nothing for other work and
/// refuses no job for its rules. A job is a <b>mail job</b> only when its <c>allowed_tools</c> explicitly grant the outlook verbs (a shell
/// rule naming <c>zyggy outlook</c>); generic rules (<c>Bash(src/*)</c>, <c>Bash(dotnet *)</c>, bare <c>Bash</c>, <c>Edit</c>,
/// <c>Write</c>, an MCP server) never make one. A mail job keeps only <c>Read</c>, <c>Grep</c>, <c>Glob</c> and the <c>zyggy outlook</c>
/// read verbs; every other rule is dropped from its run.
/// </summary>
public sealed class WorkMailToolMixTests
{
    // AC-17: the mail-reading job of Central's timer, the read verbs one by one, and file reads beside them keep every rule.
    [Theory]
    [InlineData("Bash(zyggy outlook *)")]
    [InlineData("Bash(zyggy outlook brief *)", "Bash(zyggy outlook brief-done *)", "Read", "Grep", "Glob")]
    [InlineData("Bash(zyggy outlook policy)", "Bash(zyggy outlook count *)", "Bash(zyggy outlook list *)", "Bash(zyggy outlook read *)")]
    [InlineData("Bash(zyggy outlook share *)", "Bash(zyggy outlook drafts)", "Read(notes/**)")]
    [InlineData("Bash(zyggy outlook list:*)")]
    [InlineData("Bash(zyggy.exe outlook brief --since *)")]
    public void MailRules_ReadOnlyRules_AllKept(params string[] rules)
    {
        // Act
        IReadOnlyList<string> kept = WorkMailToolMix.MailRules(rules);

        // Assert
        kept.Should().Equal(rules);
    }

    // AC-17: anything beside the read verbs and the file reads is dropped, never refused.
    [Theory]
    [InlineData("Bash")]
    [InlineData("Edit")]
    [InlineData("Bash(python *)")]
    [InlineData("Bash(dotnet *)")]
    [InlineData("Bash(src/*)")]
    [InlineData("Write")]
    [InlineData("Edit(**)")]
    [InlineData("PowerShell(Get-ChildItem)")]
    [InlineData("Task")]
    [InlineData("Agent")]
    [InlineData("mcp__outlook")]
    [InlineData("WebFetch")]
    [InlineData("Bash(zyggy run *)")]
    [InlineData("Bash(zyggy outlook send *)")]
    [InlineData("Bash(zyggy outlook move:*)")]
    [InlineData("Bash(zyggy outlook mo*)")]
    [InlineData("PowerShell(zyggy outlook list *)")]
    [InlineData("Bash(zyggy outlook list; python x.py)")]
    [InlineData("Bash(zyggy outlook list $(python x.py))")]
    [InlineData("Bash(zyggy outlook *,python *)")]
    [InlineData("Bash(C:/tools/zyggy.exe outlook list)")]
    [InlineData("Bash(ZYGGY_JOB= zyggy outlook *)")]
    public void MailRules_RuleBeyondTheReadVerbs_Dropped(string extra)
    {
        // Act
        IReadOnlyList<string> kept = WorkMailToolMix.MailRules(["Read", "Bash(zyggy outlook list *)", extra]);

        // Assert
        kept.Should().Equal("Read", "Bash(zyggy outlook list *)");
    }

    [Theory]
    [InlineData(true, "Bash(zyggy outlook *)")]
    [InlineData(true, "Read", "bash(Zyggy.exe Outlook list *)")]
    [InlineData(true, "Bash(ZYGGY_JOB= zyggy outlook *)")]
    [InlineData(true, "Read,Bash(zyggy outlook brief)")]
    [InlineData(false, "Read", "Edit", "Write", "Bash(src/*)", "Bash(dotnet *)")]
    [InlineData(false, "Read", "Edit", "Write", "Bash(dotnet *)", "Bash(git status*)", "Bash(git commit *)")]
    [InlineData(false, "Bash")]
    [InlineData(false, "PowerShell")]
    [InlineData(false, "Bash(*)")]
    [InlineData(false, "Bash(zyggy *)")]
    [InlineData(false, "Read,Bash")]
    [InlineData(false, "mcp__outlook")]
    [InlineData(false, "Bash(dotnet test --filter Zyggy.Core.Tests.Outlook)")]
    public void IsMailJob_OnlyAnExplicitOutlookRule_MakesAMailJob(bool expected, params string[] rules)
    {
        // Act
        bool mailJob = WorkMailToolMix.IsMailJob(rules);

        // Assert
        mailJob.Should().Be(expected);
    }
}
