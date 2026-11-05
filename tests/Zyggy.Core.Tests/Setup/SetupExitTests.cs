using Zyggy.Core.Setup;

namespace Zyggy.Core.Tests.Setup;

/// <summary>Spec 42 Contracts: the exit is the worst line by priority — 3 beats 4 beats 6 beats 0.</summary>
public sealed class SetupExitTests
{
    public static TheoryData<SetupLine[], int> Combinations => new()
    {
        { [], SetupExit.Ok },
        { [Line(SetupItem.NodeJson, SetupOutcome.Done), Line(SetupItem.SecretSigningKey, SetupOutcome.AlreadyDone), Line(SetupItem.SecretGitHubPat, SetupOutcome.AlreadyDone)], SetupExit.Ok },
        { [Line(SetupItem.NodeJson, SetupOutcome.LeftAsIs), Line(SetupItem.SpikeFileStore, SetupOutcome.LeftAsIs)], SetupExit.Ok },
        { [Line(SetupItem.NodeJson, SetupOutcome.Failed)], SetupExit.NodeConfig },
        { [Line(SetupItem.SecretSigningKey, SetupOutcome.Missing)], SetupExit.SigningKeyMissing },
        { [Line(SetupItem.SecretGitHubPat, SetupOutcome.Missing)], SetupExit.Incomplete },
        { [Line(SetupItem.ZyggyCore, SetupOutcome.Failed)], SetupExit.Incomplete },
        { [Line(SetupItem.SecretSigningKey, SetupOutcome.Failed)], SetupExit.Incomplete },
        { [Line(SetupItem.SecretGitHubPat, SetupOutcome.Missing), Line(SetupItem.SecretSigningKey, SetupOutcome.Missing)], SetupExit.SigningKeyMissing },
        { [Line(SetupItem.SettingsEnv, SetupOutcome.Failed), Line(SetupItem.SecretSigningKey, SetupOutcome.Missing)], SetupExit.SigningKeyMissing },
        { [Line(SetupItem.SecretSigningKey, SetupOutcome.Missing), Line(SetupItem.NodeJson, SetupOutcome.Failed)], SetupExit.NodeConfig },
        { [Line(SetupItem.SkillArchive, SetupOutcome.Failed), Line(SetupItem.NodeJson, SetupOutcome.Failed)], SetupExit.NodeConfig },
    };

    [Theory]
    [MemberData(nameof(Combinations))]
    public void Worst_Combinations_PriorityThreeFourSixZero(SetupLine[] lines, int expected)
    {
        // Act
        int exit = SetupExit.Worst(lines);

        // Assert
        exit.Should().Be(expected);
    }

    private static SetupLine Line(SetupItem item, SetupOutcome outcome) => new(item, outcome, null);
}
