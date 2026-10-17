using Zyggy.Core.Envelope;
using Zyggy.Core.Jobs;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Jobs;

/// <summary>Plan 06 Step 2 (AC-4): one run per project through an exclusive OS handle, no staleness rule.</summary>
public sealed class ProjectLockTests : IDisposable
{
    private static readonly DateTimeOffset T = new(2026, 9, 27, 14, 5, 0, TimeSpan.Zero);

    private static readonly EnvelopeId JobId = EnvelopeId.New(T);

    private readonly ScratchProject _project = ScratchProject.Create();

    public void Dispose() => _project.Dispose();

    [Fact]
    public void TryAcquire_Free_ReturnsHandleAndWritesThreeLines()
    {
        // Act
        using var held = ProjectLock.TryAcquire(_project.Root, JobId, 4242, T);

        // Assert
        held.Should().NotBeNull();
        held!.Path.Should().Be(_project.LockPath);
        File.Exists(_project.LockPath).Should().BeTrue();
        held.ReadContent().Should().Be($"job: {JobId.Value}\npid: 4242\nacquired: 2026-09-27T14:05:00Z\n");
    }

    [Fact]
    public void TryAcquire_HeldByAnotherInstance_ReturnsNull()
    {
        // Arrange
        using var first = ProjectLock.TryAcquire(_project.Root, JobId, 1, T);

        // Act
        using var second = ProjectLock.TryAcquire(_project.Root, JobId, 2, T);

        // Assert
        first.Should().NotBeNull();
        second.Should().BeNull();
    }

    [Fact]
    public void TryAcquire_AfterHolderDisposed_Succeeds()
    {
        // Arrange
        ProjectLock.TryAcquire(_project.Root, JobId, 1, T)!.Dispose();

        // Act
        using var again = ProjectLock.TryAcquire(_project.Root, JobId, 2, T);

        // Assert
        again.Should().NotBeNull();
    }

    [Fact]
    public void IsHeld_WhileHandleLives_TrueAfterDisposeFalse()
    {
        // Arrange
        var held = ProjectLock.TryAcquire(_project.Root, JobId, 1, T)!;

        // Act
        var during = ProjectLock.IsHeld(_project.Root);
        held.Dispose();
        var after = ProjectLock.IsHeld(_project.Root);

        // Assert
        during.Should().BeTrue();
        after.Should().BeFalse();
    }

    [Fact]
    public void IsHeld_NoFile_FalseAndCreatesNothing()
    {
        // Act
        var held = ProjectLock.IsHeld(_project.Root);

        // Assert
        held.Should().BeFalse();
        File.Exists(_project.LockPath).Should().BeFalse();
    }

    [Theory]
    [InlineData("2020-01-01T00:00:00Z")]
    [InlineData("2026-09-27T14:04:00Z")]
    public void TryAcquire_StaleFileOfDeadProcessAnyAge_Opens(string acquired)
    {
        // Arrange: a file left by a process that no longer exists — old or one minute ago, there is no age rule
        File.WriteAllText(_project.LockPath, $"job: 01K00000000000000000000000\npid: 999999\nacquired: {acquired}\n");

        // Act
        using var held = ProjectLock.TryAcquire(_project.Root, JobId, 7, T);

        // Assert
        held.Should().NotBeNull();
        held!.ReadContent().Should().StartWith($"job: {JobId.Value}\npid: 7\n");
    }

    [Fact]
    public void Dispose_RemovesTheFile()
    {
        // Arrange
        var held = ProjectLock.TryAcquire(_project.Root, JobId, 1, T)!;

        // Act
        held.Dispose();

        // Assert
        File.Exists(_project.LockPath).Should().BeFalse();
    }

    [Fact]
    public void CurrentProcessId_IsThisProcess()
    {
        // Act
        var pid = ProjectLock.CurrentProcessId;

        // Assert
        pid.Should().Be(Environment.ProcessId);
    }
}
