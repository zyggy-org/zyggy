using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Zyggy.Core.Processes;

namespace Zyggy.Core.Tests.Processes;

/// <summary>
/// Spec 61 slice 8, AC-26 (unit half): the node records the model processes it starts; at start-up it ends a recorded process only when
/// it provably is that run's process — the recorded pid AND start time, this user, the recorded name — and never one of a node still
/// running, nor another program (or another <c>claude</c> of the user's own sessions) that reuses the pid.
/// </summary>
public sealed class ModelProcessRecordsTests : IDisposable
{
    private static readonly DateTimeOffset NodeStart = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset ModelStart = NodeStart.AddMinutes(5);

    private static readonly ProcessFacts OldNode = new(1000, "zyggy-node", NodeStart, true);

    private static readonly ProcessFacts NewNode = new(2000, "zyggy-node", NodeStart.AddHours(1), true);

    private static readonly ProcessFacts Model = new(3000, "claude", ModelStart, true);

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("zyggy-model-records-");

    private readonly IProcessTable _table = Substitute.For<IProcessTable>();

    public void Dispose() => _dir.Delete(recursive: true);

    [Fact]
    public void AddThenRemove_RunEnded_NoRecordLeft()
    {
        // Arrange
        _table.Self().Returns(OldNode);
        ModelProcessRecords records = Records();
        string? record = records.Add(Model);

        // Act
        records.Remove(record);

        // Assert
        (record is not null, _dir.GetFiles("*", SearchOption.AllDirectories).Length).Should().Be((true, 0));
    }

    // AC-26: the recorded process still runs with the recorded start, as this user, under its name → ended, record cleared
    [Fact]
    public void EndLeftovers_RecordedModelStillRunning_EndedAndCleared()
    {
        // Arrange
        RecordFromOldNode(Model);
        _table.Find(Model.Pid).Returns(Model);
        _table.EndTree(Model.Pid, Arg.Any<TimeSpan>()).Returns((string?)null);

        // Act
        int ended = Records().EndLeftovers();

        // Assert
        (ended, _table.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IProcessTable.EndTree)), Left()).Should().Be((1, 1, 0));
    }

    // AC-26: a pid reused by another program, another user's process, or one of the user's own claude sessions with another start → untouched
    [Theory]
    [InlineData("claude", 3600, true)]
    [InlineData("notepad", 0, true)]
    [InlineData("claude", 0, false)]
    [InlineData("claude", -2, true)]
    public void EndLeftovers_PidNowAnotherProcess_NotTouchedRecordCleared(string name, int startShiftSeconds, bool sameUser)
    {
        // Arrange
        RecordFromOldNode(Model);
        _table.Find(Model.Pid).Returns(new ProcessFacts(Model.Pid, name, ModelStart.AddSeconds(startShiftSeconds), sameUser));

        // Act
        int ended = Records().EndLeftovers();

        // Assert
        _table.DidNotReceiveWithAnyArgs().EndTree(default, default);
        (ended, Left()).Should().Be((0, 0));
    }

    [Fact]
    public void EndLeftovers_StartTimeUnknown_NotTouched()
    {
        // Arrange: the system does not say when the process started, so nothing proves it is the run's process
        RecordFromOldNode(Model);
        _table.Find(Model.Pid).Returns(Model with { StartedUtc = null });

        // Act
        int ended = Records().EndLeftovers();

        // Assert
        _table.DidNotReceiveWithAnyArgs().EndTree(default, default);
        ended.Should().Be(0);
    }

    [Fact]
    public void EndLeftovers_RecordedProcessGone_NothingEndedRecordCleared()
    {
        // Arrange
        RecordFromOldNode(Model);

        // Act
        int ended = Records().EndLeftovers();

        // Assert
        _table.DidNotReceiveWithAnyArgs().EndTree(default, default);
        (ended, Left()).Should().Be((0, 0));
    }

    [Fact]
    public void EndLeftovers_NodeOfTheRecordStillRunning_NotTouchedRecordKept()
    {
        // Arrange: the node that started the model still runs (it is not a killed node's leftover)
        RecordFromOldNode(Model);
        _table.Find(OldNode.Pid).Returns(OldNode);
        _table.Find(Model.Pid).Returns(Model);

        // Act
        int ended = Records().EndLeftovers();

        // Assert
        _table.DidNotReceiveWithAnyArgs().EndTree(default, default);
        (ended, Left()).Should().Be((0, 1));
    }

    [Fact]
    public void EndLeftovers_UnreadableRecord_RemovedNothingEnded()
    {
        // Arrange
        string folder = Path.Combine(_dir.FullName, "model-processes");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "3000-1.txt"), "not a record");

        // Act
        int ended = Records().EndLeftovers();

        // Assert
        _table.DidNotReceiveWithAnyArgs().EndTree(default, default);
        (ended, Left()).Should().Be((0, 0));
    }

    private ModelProcessRecords Records() => new(Path.Combine(_dir.FullName, "model-processes"), _table, NullLogger<ModelProcessRecords>.Instance);

    private void RecordFromOldNode(ProcessFacts model)
    {
        var table = Substitute.For<IProcessTable>();
        table.Self().Returns(OldNode);
        new ModelProcessRecords(Path.Combine(_dir.FullName, "model-processes"), table, NullLogger<ModelProcessRecords>.Instance).Add(model).Should().NotBeNull();
        _table.Self().Returns(NewNode);
    }

    private int Left() => Directory.Exists(Path.Combine(_dir.FullName, "model-processes")) ? Directory.GetFiles(Path.Combine(_dir.FullName, "model-processes")).Length : 0;
}
