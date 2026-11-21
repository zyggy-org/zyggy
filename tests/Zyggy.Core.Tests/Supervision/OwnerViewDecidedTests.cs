using Zyggy.Core.Brief;
using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Supervision;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Tests.Supervision;

// AC-28 (fed with real decisions, slice 13): Central's node hands the decisions to the brief as kind, machine, project and id in
// decided.json; the brief reads the ones since the previous brief. Ids are minted from 10:00 UTC.
public sealed class OwnerViewDecidedTests : IDisposable
{
    private const string Marker = "CANARY-5d2a-never-in-the-brief";

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly MachineName WorkLaptop = MachineName.Parse("work-laptop");

    private static readonly DateTimeOffset Start = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "zyggy-decided-" + Guid.NewGuid().ToString("N"));

    private string File => Path.Combine(_directory, "decided.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static DecisionRecord Decision(DateTimeOffset at, DecisionKind kind, MachineName? to = null)
    {
        EnvelopeId id = EnvelopeId.New(at);
        return new(id, to ?? HomeLaptop, at, EnvelopeId.New(at.AddMinutes(-1)), new DecisionFields(kind, DecisionBasis.Reported, "none", Project: "calizr"), Marker, false);
    }

    private static bool IsWork(MachineName machine) => machine == WorkLaptop;

    // AC-28
    [Fact]
    public void Read_DecisionsAroundThePreviousBrief_OnlyTheLaterOnes_OldestFirst()
    {
        // Arrange
        DecisionRecord before = Decision(Start, DecisionKind.Accept);
        DecisionRecord first = Decision(Start.AddHours(2), DecisionKind.Correct);
        DecisionRecord second = Decision(Start.AddHours(3), DecisionKind.Escalate);
        DecidedFile.Write(File, [second, before, first], IsWork, Start.AddHours(4));

        // Act
        IReadOnlyList<DecidedLine> lines = DecidedFile.Read(File, Start.AddHours(1), TimeZoneInfo.Utc);

        // Assert
        lines.Should().Equal(
            new DecidedLine("2026-11-15 12:00", "correct", "home-laptop", "calizr", first.Id.Value),
            new DecidedLine("2026-11-15 13:00", "escalate", "home-laptop", "calizr", second.Id.Value));
    }

    // AC-28
    [Fact]
    public void Write_WorkMachineDecision_NoProject_AndNoDecisionText()
    {
        // Arrange
        DecisionRecord decision = Decision(Start, DecisionKind.Answer, WorkLaptop);

        // Act
        DecidedFile.Write(File, [decision], IsWork, Start.AddMinutes(5));

        // Assert
        string text = System.IO.File.ReadAllText(File);
        new { Line = DecidedFile.Read(File, Start.AddHours(-1), TimeZoneInfo.Utc).Single(), Marker = text.Contains(Marker, StringComparison.Ordinal), Project = text.Contains("calizr", StringComparison.Ordinal) }
            .Should().BeEquivalentTo(new { Line = new DecidedLine("2026-11-15 10:00", "answer", "work-laptop", null, decision.Id.Value), Marker = false, Project = false });
    }

    // AC-28
    [Fact]
    public void Write_DecisionOlderThan14Days_LeftOut()
    {
        // Arrange
        DecisionRecord old = Decision(Start, DecisionKind.Accept);
        DecisionRecord recent = Decision(Start.AddDays(14), DecisionKind.Accept);

        // Act
        DecidedFile.Write(File, [old, recent], IsWork, Start.AddDays(14).AddHours(1));

        // Assert
        DecidedFile.Read(File, DateTimeOffset.MinValue, TimeZoneInfo.Utc).Select(l => l.Id).Should().Equal(recent.Id.Value);
    }

    // AC-28
    [Fact]
    public void Write_SameDecisionsTwice_SecondWriteSkipped()
    {
        // Arrange
        DecisionRecord decision = Decision(Start, DecisionKind.Accept);
        DecidedFile.Write(File, [decision], IsWork, Start.AddMinutes(1));

        // Act
        bool written = DecidedFile.Write(File, [decision], IsWork, Start.AddMinutes(2));

        // Assert
        written.Should().BeFalse();
    }

    // AC-28
    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("[{\"time\":\"bad\"}]")]
    public void Read_MissingOrUnreadableFile_NoLines(string? content)
    {
        // Arrange
        if (content is not null)
        {
            Directory.CreateDirectory(_directory);
            System.IO.File.WriteAllText(File, content);
        }

        // Act
        IReadOnlyList<DecidedLine> lines = DecidedFile.Read(File, DateTimeOffset.MinValue, TimeZoneInfo.Utc);

        // Assert
        lines.Should().BeEmpty();
    }
}
