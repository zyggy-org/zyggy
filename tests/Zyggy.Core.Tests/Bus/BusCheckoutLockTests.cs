using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Bus;

namespace Zyggy.Core.Tests.Bus;

/// <summary>Patch 0.8.1 (plan 04 Step 22): one git writer per checkout across processes.</summary>
public sealed class BusCheckoutLockTests : IDisposable
{
    private readonly string _checkout = Path.Combine(Path.GetTempPath(), "zyggy-unit", Guid.NewGuid().ToString("N"));

    private readonly FakeTimeProvider _clock = new();

    public BusCheckoutLockTests() => Directory.CreateDirectory(Path.Combine(_checkout, ".git"));

    public void Dispose()
    {
        if (Directory.Exists(_checkout))
        {
            Directory.Delete(_checkout, recursive: true);
        }
    }

    [Fact]
    public async Task AcquireAsync_Free_ReturnsHandleAndCreatesLockFileInGitDir()
    {
        // Act
        using IDisposable? handle = await BusCheckoutLock.AcquireAsync(_checkout, BusCheckoutLock.WriteLockName, TimeSpan.FromSeconds(5), _clock, TestContext.Current.CancellationToken);

        // Assert
        handle.Should().NotBeNull();
        File.Exists(Path.Combine(_checkout, ".git", BusCheckoutLock.WriteLockName)).Should().BeTrue();
    }

    [Fact]
    public async Task AcquireAsync_Released_LockFileGoneAndReacquirable()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        IDisposable? first = await BusCheckoutLock.AcquireAsync(_checkout, BusCheckoutLock.WriteLockName, TimeSpan.FromSeconds(5), _clock, ct);

        // Act
        first!.Dispose();
        using IDisposable? second = await BusCheckoutLock.AcquireAsync(_checkout, BusCheckoutLock.WriteLockName, TimeSpan.FromSeconds(5), _clock, ct);

        // Assert
        second.Should().NotBeNull();
    }

    [Fact]
    public async Task AcquireAsync_Held_WaitsUntilReleased()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        IDisposable? first = await BusCheckoutLock.AcquireAsync(_checkout, BusCheckoutLock.WriteLockName, TimeSpan.FromSeconds(30), _clock, ct);

        // Act
        Task<IDisposable?> waiting = BusCheckoutLock.AcquireAsync(_checkout, BusCheckoutLock.WriteLockName, TimeSpan.FromSeconds(30), _clock, ct);
        _clock.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(50, ct);
        bool stillWaiting = !waiting.IsCompleted;
        first!.Dispose();
        _clock.Advance(TimeSpan.FromSeconds(1));
        using IDisposable? second = await waiting.WaitAsync(TimeSpan.FromSeconds(10), ct);

        // Assert
        stillWaiting.Should().BeTrue();
        second.Should().NotBeNull();
    }

    [Fact]
    public async Task AcquireAsync_HeldPastTimeout_ReturnsNull()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using IDisposable? first = await BusCheckoutLock.AcquireAsync(_checkout, BusCheckoutLock.WriteLockName, TimeSpan.FromSeconds(5), _clock, ct);

        // Act
        Task<IDisposable?> waiting = BusCheckoutLock.AcquireAsync(_checkout, BusCheckoutLock.WriteLockName, TimeSpan.FromSeconds(3), _clock, ct);
        for (int i = 0; i < 20 && !waiting.IsCompleted; i++)
        {
            _clock.Advance(TimeSpan.FromMilliseconds(500));
            await Task.Delay(10, ct);
        }

        // Assert
        (await waiting.WaitAsync(TimeSpan.FromSeconds(10), ct)).Should().BeNull();
    }

    [Fact]
    public async Task AcquireAsync_ZeroTimeout_FailsImmediatelyWhenHeld()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using IDisposable? first = await BusCheckoutLock.AcquireAsync(_checkout, BusCheckoutLock.ListenLockName, TimeSpan.Zero, _clock, ct);

        // Act
        IDisposable? second = await BusCheckoutLock.AcquireAsync(_checkout, BusCheckoutLock.ListenLockName, TimeSpan.Zero, _clock, ct);

        // Assert
        first.Should().NotBeNull();
        second.Should().BeNull();
    }

    [Fact]
    public async Task AcquireAsync_WriteAndListenLocks_AreIndependent()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using IDisposable? listen = await BusCheckoutLock.AcquireAsync(_checkout, BusCheckoutLock.ListenLockName, TimeSpan.Zero, _clock, ct);

        // Act
        using IDisposable? write = await BusCheckoutLock.AcquireAsync(_checkout, BusCheckoutLock.WriteLockName, TimeSpan.Zero, _clock, ct);

        // Assert
        write.Should().NotBeNull();
    }

    [Fact]
    public async Task AcquireAsync_NoGitDirectory_NoOpHandle()
    {
        // Arrange: not a repository — the start-up check reports that; the lock does not create a .git directory
        string plain = Path.Combine(Path.GetTempPath(), "zyggy-unit", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(plain);
        try
        {
            // Act
            using IDisposable? handle = await BusCheckoutLock.AcquireAsync(plain, BusCheckoutLock.WriteLockName, TimeSpan.Zero, _clock, TestContext.Current.CancellationToken);

            // Assert
            handle.Should().NotBeNull();
            Directory.Exists(Path.Combine(plain, ".git")).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(plain, recursive: true);
        }
    }

    [Theory]
    [InlineData("fatal: Cannot rebase onto multiple branches.", true)]
    [InlineData("error: cannot lock ref 'refs/remotes/origin/main': is at f934 but expected 6196", true)]
    [InlineData("warning: fetch updated the current branch head.", true)]
    [InlineData("fatal: Unable to create '/bus/.git/index.lock': File exists.", true)]
    [InlineData("fatal: unable to access 'https://example.invalid/': Could not resolve host", false)]
    [InlineData("", false)]
    public void IsConcurrentChange_GitMessages(string stderr, bool expected)
    {
        // Act & Assert
        BusCheckoutLock.IsConcurrentChange(stderr).Should().Be(expected);
    }
}
