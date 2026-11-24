using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Zyggy.Core.Processes;

namespace Zyggy.Core.Tests.Processes;

/// <summary>
/// Spec 61 slice 8, AC-24 (unit half): <c>zyggy-node --exit-with-parent</c> watches the process that started it — the scheduled task's
/// wrapper — and returns as soon as that process is gone, also when its pid now belongs to a later process; a parent already gone at
/// start returns at once.
/// </summary>
public sealed class ParentWatchTests
{
    private static readonly DateTimeOffset T = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);

    private static readonly ProcessFacts Wrapper = new(500, "powershell", T.AddSeconds(-2), true);

    private static readonly ProcessFacts Node = new(600, "zyggy-node", T.AddSeconds(-1), true);

    private readonly IProcessTable _table = Substitute.For<IProcessTable>();

    private readonly FakeTimeProvider _clock = new(T);

    public ParentWatchTests() => _table.Self().Returns(Node);

    [Fact]
    public async Task Wait_ParentRunning_DoesNotReturn()
    {
        // Arrange
        _table.Find(Wrapper.Pid).Returns(Wrapper);
        Task wait = Watch(Wrapper.Pid).WaitForParentExitAsync(TestContext.Current.CancellationToken);

        // Act
        await AdvanceAsync(TimeSpan.FromMinutes(5));

        // Assert
        wait.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task Wait_ParentEnds_ReturnsAtTheNextLook()
    {
        // Arrange
        _table.Find(Wrapper.Pid).Returns(Wrapper);
        Task wait = Watch(Wrapper.Pid).WaitForParentExitAsync(TestContext.Current.CancellationToken);
        await AdvanceAsync(TimeSpan.FromSeconds(3));
        _table.Find(Wrapper.Pid).Returns((ProcessFacts?)null);

        // Act
        await AdvanceAsync(ParentWatch.Interval);

        // Assert
        await wait.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        wait.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task Wait_ParentPidTakenByALaterProcess_Returns()
    {
        // Arrange
        _table.Find(Wrapper.Pid).Returns(Wrapper);
        Task wait = Watch(Wrapper.Pid).WaitForParentExitAsync(TestContext.Current.CancellationToken);
        await AdvanceAsync(TimeSpan.FromSeconds(3));
        _table.Find(Wrapper.Pid).Returns(Wrapper with { Name = "notepad", StartedUtc = T.AddSeconds(2) });

        // Act
        await AdvanceAsync(ParentWatch.Interval);

        // Assert
        await wait.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        wait.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(500)]
    public async Task Wait_ParentGoneAtStart_ReturnsAtOnce(int? parentPid)
    {
        // Arrange: no parent known, the Linux init process (an orphan's parent), or a pid with no process
        _table.Find(Arg.Any<int>()).Returns((ProcessFacts?)null);

        // Act
        Task wait = Watch(parentPid).WaitForParentExitAsync(TestContext.Current.CancellationToken);

        // Assert
        await wait.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        wait.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task Wait_ParentStartedAfterTheNode_ReturnsAtOnce()
    {
        // Arrange: the recorded parent pid now belongs to a process younger than the node — the real parent is gone (Windows keeps the pid)
        _table.Find(Wrapper.Pid).Returns(Wrapper with { StartedUtc = T.AddMinutes(1) });

        // Act
        Task wait = Watch(Wrapper.Pid).WaitForParentExitAsync(TestContext.Current.CancellationToken);

        // Assert
        await wait.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        wait.IsCompletedSuccessfully.Should().BeTrue();
    }

    private ParentWatch Watch(int? parentPid) => new(_table, _ => parentPid, _clock, NullLogger<ParentWatch>.Instance);

    private async Task AdvanceAsync(TimeSpan by)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < by; elapsed += ParentWatch.Interval)
        {
            await Task.Yield();
            _clock.Advance(ParentWatch.Interval);
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
