using Zyggy.Core.Memory;

namespace Zyggy.Core.Tests.Memory;

/// <summary>Spec 38 CLI: one offload run at a time, an OS lock released when the holder ends.</summary>
public sealed class OffloadLockTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "zyggy-ut", Guid.NewGuid().ToString("N"), "archive", "offload.lock");

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(_path))!, recursive: true);

    [Fact]
    public void TryAcquire_SecondHolder_Null()
    {
        // Act
        using var first = OffloadLock.TryAcquire(_path);
        using var second = OffloadLock.TryAcquire(_path);

        // Assert
        first.Should().NotBeNull();
        second.Should().BeNull();
    }

    [Fact]
    public void Dispose_Releases()
    {
        // Arrange
        OffloadLock.TryAcquire(_path)!.Dispose();

        // Act
        using var again = OffloadLock.TryAcquire(_path);

        // Assert
        again.Should().NotBeNull();
    }
}
