using Zyggy.Core.Registry;

namespace Zyggy.Core.Tests.Registry;

public sealed class RegistrySnapshotTests
{
    [Fact]
    public void Current_BeforeReplace_Null()
    {
        // Act + Assert
        new RegistrySnapshot().Current.Should().BeNull();
    }

    [Fact]
    public void Replace_SetsCurrent()
    {
        // Arrange
        var snapshot = new RegistrySnapshot();
        RegistryDocument document = RegistryDocumentTests.HomeLaptop();

        // Act
        snapshot.Replace(document);

        // Assert
        snapshot.Current.Should().BeSameAs(document);
    }

    [Fact]
    public async Task Replace_ConcurrentReaders_SeeWholeDocuments()
    {
        // Arrange
        var snapshot = new RegistrySnapshot();
        RegistryDocument a = RegistryDocumentTests.HomeLaptop();
        RegistryDocument b = RegistryDocumentTests.Central();
        var ct = TestContext.Current.CancellationToken;

        // Act
        Task writer = Task.Run(() =>
        {
            for (int i = 0; i < 10_000; i++)
            {
                snapshot.Replace(i % 2 == 0 ? a : b);
            }
        }, ct);
        var seen = new List<RegistryDocument?>();
        Task reader = Task.Run(() =>
        {
            for (int i = 0; i < 10_000; i++)
            {
                seen.Add(snapshot.Current);
            }
        }, ct);
        await Task.WhenAll(writer, reader);

        // Assert
        seen.Should().OnlyContain(d => d == null || ReferenceEquals(d, a) || ReferenceEquals(d, b));
    }
}
