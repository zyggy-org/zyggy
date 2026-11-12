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

    [Fact]
    public void TryResolve_LabelKnown_Path()
    {
        // Arrange
        string folder = Path.Combine(Path.GetTempPath(), "zyggy-unit", "calizr");
        RegistrySnapshot snapshot = With(new RegistryProject("calizr", folder.Replace('\\', '/'), "main", [], [], null));

        // Act
        bool found = snapshot.TryResolve("calizr", out string? path);

        // Assert
        found.Should().BeTrue();
        path.Should().Be(Path.GetFullPath(folder));
    }

    [Theory]
    [InlineData("Calizr")]
    [InlineData("calizr/x")]
    [InlineData("")]
    [InlineData("unknown")]
    public void TryResolve_NotALabelOrUnknown_False(string name)
    {
        // Arrange
        RegistrySnapshot snapshot = With(new RegistryProject("calizr", Path.GetTempPath().Replace('\\', '/'), "main", [], [], null));

        // Act
        bool found = snapshot.TryResolve(name, out string? path);

        // Assert
        found.Should().BeFalse();
        path.Should().BeNull();
    }

    [Fact]
    public void TryResolve_NamesEntryWithoutPath_False()
    {
        // Arrange: a `detail: names` scan publishes no path, so it can never route
        RegistrySnapshot snapshot = With(new RegistryProject("calizr", null, null, [], [], null));

        // Act + Assert
        snapshot.TryResolve("calizr", out _).Should().BeFalse();
    }

    [Fact]
    public void TryResolve_BeforeFirstScan_False()
    {
        // Act + Assert
        new RegistrySnapshot().TryResolve("calizr", out _).Should().BeFalse();
    }

    internal static RegistrySnapshot With(params RegistryProject[] projects)
    {
        var snapshot = new RegistrySnapshot();
        snapshot.Replace(RegistryDocumentTests.Central() with { Projects = projects });
        return snapshot;
    }
}
