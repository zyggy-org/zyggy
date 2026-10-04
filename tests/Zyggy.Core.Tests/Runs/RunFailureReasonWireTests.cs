using Zyggy.Core.Runs;

namespace Zyggy.Core.Tests.Runs;

public sealed class RunFailureReasonWireTests
{
    public static TheoryData<RunFailureReason, string> Reasons => new()
    {
        { RunFailureReason.UnknownProject, "unknown_project" },
        { RunFailureReason.UnknownAgent, "unknown_agent" },
        { RunFailureReason.Locked, "locked" },
        { RunFailureReason.Timeout, "timeout" },
        { RunFailureReason.DlpFilter, "dlp_filter" },
        { RunFailureReason.ClaudeError, "claude_error" },
        { RunFailureReason.GitError, "git_error" },
        { RunFailureReason.SchemaUnsupported, "schema_unsupported" },
        { RunFailureReason.BudgetExceeded, "budget_exceeded" },
    };

    [Theory]
    [MemberData(nameof(Reasons))]
    public void ToWire_Member_ReturnsSnakeCase(RunFailureReason member, string wire)
    {
        // Act
        string text = RunFailureReasonWire.ToWire(member);

        // Assert
        text.Should().Be(wire);
    }

    [Theory]
    [MemberData(nameof(Reasons))]
    public void TryFromWire_SnakeCase_ReturnsMember(RunFailureReason member, string wire)
    {
        // Act
        bool ok = RunFailureReasonWire.TryFromWire(wire, out RunFailureReason back);

        // Assert
        ok.Should().BeTrue();
        back.Should().Be(member);
    }

    [Theory]
    [InlineData("Timeout")]
    [InlineData("budget-exceeded")]
    [InlineData("")]
    [InlineData(null)]
    public void TryFromWire_Unknown_ReturnsFalse(string? wire)
    {
        // Act
        bool ok = RunFailureReasonWire.TryFromWire(wire, out _);

        // Assert
        ok.Should().BeFalse();
    }

    [Fact]
    public void EveryMember_HasATestRow()
    {
        // Assert
        Reasons.Count.Should().Be(Enum.GetValues<RunFailureReason>().Length).And.Be(9);
    }
}
