namespace Zyggy.Core.Tests.Infrastructure;

/// <summary>
/// Spec 61 slice 9, AC-30: the READMEs carry the runbook rows the spec's Failure modes table names, and the new verbs; the existing
/// source-hygiene tests keep real machine names, tenants and ids out of published strings and fixtures.
/// </summary>
public sealed class ReliabilityRunbookTests
{
    // AC-30: every runbook entry of spec 61's Failure modes table is a row title in the README that owns it
    [Theory]
    [InlineData("Zyggy.Node", "| Report: kept, not yet written |")]
    [InlineData("Zyggy.Node", "| Sweep: a job ran twice |")]
    [InlineData("Zyggy.Node", "| Withdraw: the run was not stopped |")]
    [InlineData("Zyggy.Node", "| Withdraw: not accepted |")]
    [InlineData("Zyggy.Node", "| Node: refused to start |")]
    [InlineData("Zyggy.Node", "| Node: still running after the task was stopped |")]
    [InlineData("Zyggy.Node", "| Node: model processes of a killed node |")]
    [InlineData("Zyggy.Core", "| Jobs: stale worktree folder |")]
    [InlineData("Zyggy.Core", "| Jobs: timeout after a sleep |")]
    [InlineData("Zyggy.Core", "| Jobs: git_error before the run |")]
    public void Readme_FailureModeOfSpec61_HasItsRunbookRow(string project, string row)
    {
        // Act
        string readme = File.ReadAllText(Path.Combine(RepoRoot.Find(), "src", project, "README.md"));

        // Assert
        readme.Should().Contain(row);
    }

    // AC-30: the slice 9 behaviour is in the rows: the recovered commit, the kept list, and worktree_add saying why
    [Theory]
    [InlineData("Zyggy.Core", "zyggy job <id> (recovered)")]
    [InlineData("Zyggy.Core", "git_error: worktree_add (<git's first error line>)")]
    [InlineData("Zyggy.Core", "worktree kept at <path>")]
    [InlineData("Zyggy.Core", "kept worktrees: <n>")]
    [InlineData("Zyggy.Cli", "kept worktrees: <n>")]
    [InlineData("Zyggy.Node", "kept-worktrees.txt")]
    public void Readme_LeftoverWorktrees_NamesTheBehaviour(string project, string text)
    {
        // Act
        string readme = File.ReadAllText(Path.Combine(RepoRoot.Find(), "src", project, "README.md"));

        // Assert
        readme.Should().Contain(text);
    }

    // AC-30: the new verbs of spec 61 are rows of the CLI README's verb table
    [Theory]
    [InlineData("| `withdraw <id>")]
    [InlineData("| `node stop ")]
    public void CliReadme_Spec61Verb_HasItsRow(string row)
    {
        // Act
        string readme = File.ReadAllText(Path.Combine(RepoRoot.Find(), "src", "Zyggy.Cli", "README.md"));

        // Assert
        readme.Should().Contain(row);
    }
}
