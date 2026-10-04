using Zyggy.Core.Dream;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Dream;

/// <summary>AC-8 (unit): one run at a time through an OS file lock, released when its holder goes away.</summary>
public sealed class DreamLockTests
{
    [Fact]
    public void TryAcquire_Free_ReturnsLock()
    {
        // Arrange
        using var tree = new MemoryTree();
        var path = Path.Combine(tree.Root, "state", "dream.lock");

        // Act
        using var held = DreamLock.TryAcquire(path);

        // Assert
        held.Should().NotBeNull();
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public void TryAcquire_HeldByAnotherHandle_ReturnsNull()
    {
        // Arrange
        using var tree = new MemoryTree();
        var path = Path.Combine(tree.Root, "dream.lock");
        using var first = DreamLock.TryAcquire(path);

        // Act
        using var second = DreamLock.TryAcquire(path);

        // Assert
        first.Should().NotBeNull();
        second.Should().BeNull();
    }

    [Fact]
    public void TryAcquire_AfterHolderDisposed_Succeeds()
    {
        // Arrange
        using var tree = new MemoryTree();
        var path = Path.Combine(tree.Root, "dream.lock");
        DreamLock.TryAcquire(path)!.Dispose();

        // Act
        using var again = DreamLock.TryAcquire(path);

        // Assert
        again.Should().NotBeNull();
    }
}
