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

    // AC-3 (spec 56): changed on purpose — a `detail: names` scan publishes no path, but the node keeps the folder beside the document.
    [Fact]
    public void TryResolve_NamesEntryWithoutPath_FolderKeptInMemory()
    {
        // Arrange
        string folder = Path.Combine(Path.GetTempPath(), "zyggy-unit", "Clients", "AcmePortal");
        RegistrySnapshot snapshot = NamesOnly(("acmeportal", folder));

        // Act
        bool found = snapshot.TryResolve("acmeportal", out string? path);

        // Assert
        found.Should().BeTrue();
        path.Should().Be(Path.GetFullPath(folder));
    }

    // AC-3 (spec 56): the published document stays path-less; the folder is beside it.
    [Fact]
    public void Replace_WithFolders_CurrentCarriesNoPath()
    {
        // Arrange
        string folder = Path.Combine(Path.GetTempPath(), "zyggy-unit", "Clients", "AcmePortal");

        // Act
        RegistrySnapshot snapshot = NamesOnly(("acmeportal", folder));

        // Assert
        snapshot.Current!.Projects.Should().OnlyContain(p => p.Path == null);
    }

    // AC-3 (spec 56): on a names-only snapshot an unknown name, a non-label and an empty value still do not resolve.
    [Theory]
    [InlineData("AcmePortal")]
    [InlineData("acmeportal/x")]
    [InlineData("")]
    [InlineData("unknown")]
    public void TryResolve_NamesOnlyNotALabelOrUnknown_False(string name)
    {
        // Arrange
        RegistrySnapshot snapshot = NamesOnly(("acmeportal", Path.Combine(Path.GetTempPath(), "zyggy-unit", "Clients", "AcmePortal")));

        // Act
        bool found = snapshot.TryResolve(name, out string? path);

        // Assert
        found.Should().BeFalse();
        path.Should().BeNull();
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

    /// <summary>A snapshot as a <c>detail: names</c> scan leaves it: path-less entries, the folders kept beside the document.</summary>
    internal static RegistrySnapshot NamesOnly(params (string Name, string Folder)[] projects)
    {
        var snapshot = new RegistrySnapshot();
        snapshot.Replace(
            RegistryDocumentTests.Central() with { Projects = [.. projects.Select(p => new RegistryProject(p.Name, null, "main", [], [], null))] },
            projects.ToDictionary(p => p.Name, p => p.Folder, StringComparer.Ordinal));
        return snapshot;
    }
}
