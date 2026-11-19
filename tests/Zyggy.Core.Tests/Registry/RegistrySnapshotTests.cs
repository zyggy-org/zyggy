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

    // AC-6 (spec 56): a name two folders derive at the same level never resolves — the node never picks one.
    [Fact]
    public void TryResolve_AmbiguousName_False()
    {
        // Arrange
        RegistrySnapshot snapshot = Ambiguous("devops");

        // Act
        bool found = snapshot.TryResolve("devops", out string? path);

        // Assert
        found.Should().BeFalse();
        path.Should().BeNull();
    }

    // AC-6 (spec 56): the snapshot remembers the tie, so pre-flight can say why.
    [Fact]
    public void IsAmbiguous_TiedName_True()
    {
        // Act + Assert
        Ambiguous("devops").IsAmbiguous("devops").Should().BeTrue();
    }

    // AC-6 (spec 56): a unique name, an unknown name, a non-label and a lookup before the first scan are not ambiguous.
    [Theory]
    [InlineData("acmeportal")]
    [InlineData("unknown")]
    [InlineData("DevOps")]
    [InlineData("")]
    public void IsAmbiguous_NotATiedName_False(string name)
    {
        // Act + Assert
        Ambiguous("devops").IsAmbiguous(name).Should().BeFalse();
    }

    // AC-6 (spec 56)
    [Fact]
    public void IsAmbiguous_BeforeFirstScan_False()
    {
        // Act + Assert
        new RegistrySnapshot().IsAmbiguous("devops").Should().BeFalse();
    }

    // AC-6 (spec 56): a tie in one scan and a unique name in the next resolves after the next scan.
    [Fact]
    public void Replace_TieThenUnique_ResolvesAndNoLongerAmbiguous()
    {
        // Arrange
        string folder = Path.Combine(Path.GetTempPath(), "zyggy-unit", "devops");
        RegistrySnapshot snapshot = Ambiguous("devops");

        // Act
        snapshot.Replace(
            RegistryDocumentTests.Central() with { Projects = [new RegistryProject("devops", null, "main", [], [], null)] },
            new Dictionary<string, string>(StringComparer.Ordinal) { ["devops"] = folder });

        // Assert
        (snapshot.TryResolve("devops", out string? path), path, snapshot.IsAmbiguous("devops"))
            .Should().Be((true, Path.GetFullPath(folder), false));
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

    /// <summary>
    /// A snapshot as a scan with a same-level tie leaves it: the tied names neither published nor kept with a folder, but remembered as
    /// ambiguous; one unique project <c>acmeportal</c> beside them.
    /// </summary>
    internal static RegistrySnapshot Ambiguous(params string[] tied)
    {
        string folder = Path.Combine(Path.GetTempPath(), "zyggy-unit", "Clients", "AcmePortal");
        var snapshot = new RegistrySnapshot();
        snapshot.Replace(
            RegistryDocumentTests.Central() with { Projects = [new RegistryProject("acmeportal", null, "main", [], [], null)] },
            new Dictionary<string, string>(StringComparer.Ordinal) { ["acmeportal"] = folder },
            tied.ToHashSet(StringComparer.Ordinal));
        return snapshot;
    }
}
