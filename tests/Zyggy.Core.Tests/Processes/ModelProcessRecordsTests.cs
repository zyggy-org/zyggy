using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Zyggy.Core.Bus;
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
        int ended = Records().EndLeftovers(PreviousHolder);

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
        int ended = Records().EndLeftovers(PreviousHolder);

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
        int ended = Records().EndLeftovers(PreviousHolder);

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
        int ended = Records().EndLeftovers(PreviousHolder);

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
        int ended = Records().EndLeftovers(PreviousHolder);

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
        int ended = Records().EndLeftovers(PreviousHolder);

        // Assert
        _table.DidNotReceiveWithAnyArgs().EndTree(default, default);
        (ended, Left()).Should().Be((0, 0));
    }

    // Hardening (slice 8 verify): the name to match is the configured model executable, never the record's; a planted record naming
    // another program (an editor) that runs under the recorded pid and start, of a node that is the previous listener → never ended.
    [Fact]
    public void EndLeftovers_PlantedRecordNamingAnotherProgram_NotEnded()
    {
        // Arrange
        var editor = new ProcessFacts(4000, "notepad", ModelStart, true);
        Plant(editor, OldNode);
        _table.Find(editor.Pid).Returns(editor);
        _table.EndTree(editor.Pid, Arg.Any<TimeSpan>()).Returns((string?)null);

        // Act
        int ended = Records().EndLeftovers(PreviousHolder);

        // Assert
        _table.DidNotReceiveWithAnyArgs().EndTree(default, default);
        (ended, Left()).Should().Be((0, 0));
    }

    // Hardening: a record whose node is not this checkout's previous listener as the listener itself recorded it (an interactive claude
    // of the user's own, in another folder, planted with a made-up node) → never ended, record cleared.
    [Theory]
    [InlineData(5000, 0)]
    [InlineData(1000, 60)]
    public void EndLeftovers_RecordOfAnotherNodeThanThePreviousListener_NotEnded(int nodePid, int nodeStartShiftSeconds)
    {
        // Arrange
        var interactive = new ProcessFacts(4100, "claude", ModelStart, true);
        Plant(interactive, new ProcessFacts(nodePid, "zyggy-node", NodeStart.AddSeconds(nodeStartShiftSeconds), true));
        _table.Find(interactive.Pid).Returns(interactive);
        _table.EndTree(interactive.Pid, Arg.Any<TimeSpan>()).Returns((string?)null);

        // Act
        int ended = Records().EndLeftovers(PreviousHolder);

        // Assert
        _table.DidNotReceiveWithAnyArgs().EndTree(default, default);
        (ended, Left()).Should().Be((0, 0));
    }

    // Hardening: no previous listener record (none found when the lock was taken) → nothing is ended
    [Fact]
    public void EndLeftovers_NoPreviousListenerRecord_NothingEnded()
    {
        // Arrange
        RecordFromOldNode(Model);
        _table.Find(Model.Pid).Returns(Model);
        _table.EndTree(Model.Pid, Arg.Any<TimeSpan>()).Returns((string?)null);

        // Act
        int ended = Records().EndLeftovers(previousListener: null);

        // Assert
        _table.DidNotReceiveWithAnyArgs().EndTree(default, default);
        (ended, Left()).Should().Be((0, 0));
    }

    // Linux reports at most 15 characters of a process name: a configured executable with a longer name still matches its truncation
    [Fact]
    public void EndLeftovers_LinuxTruncatedNameOfTheConfiguredExecutable_Ended()
    {
        // Arrange
        var model = new ProcessFacts(4200, "claude-code-wra", ModelStart, true);
        RecordFromOldNode(model);
        _table.Find(model.Pid).Returns(model);
        _table.EndTree(model.Pid, Arg.Any<TimeSpan>()).Returns((string?)null);

        // Act
        int ended = new ModelProcessRecords(Path.Combine(_dir.FullName, "model-processes"), "/usr/local/bin/claude-code-wrapper", _table, NullLogger<ModelProcessRecords>.Instance)
            .EndLeftovers(PreviousHolder);

        // Assert
        ended.Should().Be(1);
    }

    private static ListenerRecord PreviousHolder => new(OldNode.Pid, OldNode.StartedUtc!.Value, "0.30.1", NodeStart.AddSeconds(2));

    private ModelProcessRecords Records() => new(Path.Combine(_dir.FullName, "model-processes"), ClaudePath, _table, NullLogger<ModelProcessRecords>.Instance);

    private const string ClaudePath = "C:/tools/claude.exe";

    private void Plant(ProcessFacts process, ProcessFacts node)
    {
        string folder = Path.Combine(_dir.FullName, "model-processes");
        Directory.CreateDirectory(folder);
        File.WriteAllText(
            Path.Combine(folder, process.Pid + "-1.txt"),
            $"pid={process.Pid}\nstarted={process.StartedUtc!.Value.UtcDateTime:O}\nname={process.Name}\nnode_pid={node.Pid}\nnode_started={node.StartedUtc!.Value.UtcDateTime:O}\n");
        _table.Self().Returns(NewNode);
    }

    private void RecordFromOldNode(ProcessFacts model)
    {
        var table = Substitute.For<IProcessTable>();
        table.Self().Returns(OldNode);
        new ModelProcessRecords(Path.Combine(_dir.FullName, "model-processes"), ClaudePath, table, NullLogger<ModelProcessRecords>.Instance).Add(model).Should().NotBeNull();
        _table.Self().Returns(NewNode);
    }

    private int Left() => Directory.Exists(Path.Combine(_dir.FullName, "model-processes")) ? Directory.GetFiles(Path.Combine(_dir.FullName, "model-processes")).Length : 0;
}
