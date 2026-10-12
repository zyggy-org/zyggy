using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Memory;

/// <summary>
/// Plan 39 Step 4 (AC-15, AC-3 integration): every Office refusal of <c>zyggy memory archive add</c> from the built binary exits 2 with
/// its detail word and leaves the clone's tree, index and inbox and the bare remote byte-for-byte unchanged; with the instance's six-only
/// <c>archive.json</c> a docx is <c>type_not_allowed</c>, with the nine-type file it is archived.
/// </summary>
public sealed class ArchiveAddOfficeRefusalTests : IAsyncLifetime
{
    // The instance's current archive.json allow-list (zyggy-geoffrey instance/archive.json before plan 39 Step 7).
    private const string SixOnly = """{"allowed_types": ["text/plain", "text/markdown", "application/pdf", "image/png", "image/jpeg", "image/gif"]}""";

    private const string NineTypes = """
        {"allowed_types": ["text/plain", "text/markdown", "application/pdf", "image/png", "image/jpeg", "image/gif",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation"]}
        """;

    private readonly MemoryRepoFixture _repo = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _repo.InitializeAsync();
        await _repo.SeedAsync("archive", Ct);
    }

    public ValueTask DisposeAsync() => _repo.DisposeAsync();

    private Task<ZyggyRun> AddAsync(string source, IReadOnlyDictionary<string, string?>? env = null) =>
        _repo.ArchiveAsync(Ct, env, ["add", "--project", "zyggy", "--name", "Roof offer", "--description", "Contractor offer for the roof repair", "--file", source]);

    private async Task<(ZyggyRun Run, string Before, string After, int Commits)> RefusedAsync(string source, IReadOnlyDictionary<string, string?>? env = null)
    {
        var before = await _repo.TreeFingerprintAsync(Ct);
        var commits = await _repo.CommitCountAsync(Ct);
        var run = await AddAsync(source, env);
        return (run, before, await _repo.TreeFingerprintAsync(Ct), commits);
    }

    private async Task<string> ConfigAsync(string json)
    {
        var config = Path.Combine(_repo.RootDir, "archive.json");
        await File.WriteAllTextAsync(config, json, Ct);
        return config;
    }

    [Theory]
    [InlineData("sample.docm", "office_refused (macro_enabled)")]
    [InlineData("sample.doc", "office_refused (legacy_binary)")]
    [InlineData("sample.odt", "office_refused (opendocument)")]
    [InlineData("plain.zip", "office_refused (not_office)")]
    public async Task Add_RefusedOffice_ExitTwoDetailTreeAndRemoteUnchanged(string sample, string reason)
    {
        // Arrange
        var source = await _repo.WriteSourceAsync(sample, ArchiveAddOfficeCommandTests.Sample(sample));

        // Act
        var (run, before, after, commits) = await RefusedAsync(source);

        // Assert
        run.ExitCode.Should().Be(2, run.Stderr + run.Stdout);
        run.Stdout.Should().BeEmpty();
        run.Stderr.Should().Be($"archive: refused: {reason}\n");
        after.Should().Be(before);
        (await _repo.StatusAsync(Ct)).Should().BeEmpty();
        (await _repo.CommitCountAsync(Ct)).Should().Be(commits);
        Directory.Exists(Path.Combine(_repo.PrincipalDir, "inbox")).Should().BeFalse();
    }

    [Fact]
    public async Task Add_DocmRenamedDocx_MacroEnabled()
    {
        // Arrange: the name is never consulted.
        var source = await _repo.WriteSourceAsync("offer.docx", ArchiveAddOfficeCommandTests.Sample("sample.docm"));

        // Act
        var (run, before, after, _) = await RefusedAsync(source);

        // Assert
        run.ExitCode.Should().Be(2, run.Stderr + run.Stdout);
        run.Stderr.Should().Be("archive: refused: office_refused (macro_enabled)\n");
        after.Should().Be(before);
    }

    [Fact]
    public async Task Add_DocxWithSixOnlyArchiveJson_ExitTwoTypeNotAllowedNamesWire()
    {
        // Arrange
        var source = await _repo.WriteSourceAsync("sample.docx", ArchiveAddOfficeCommandTests.Sample("sample.docx"));
        var env = new Dictionary<string, string?> { ["ZYGGY_ARCHIVE_CONFIG"] = await ConfigAsync(SixOnly) };

        // Act
        var (run, before, after, _) = await RefusedAsync(source, env);

        // Assert
        run.ExitCode.Should().Be(2, run.Stderr + run.Stdout);
        run.Stdout.Should().BeEmpty();
        run.Stderr.Should().Be("archive: refused: type_not_allowed (application/vnd.openxmlformats-officedocument.wordprocessingml.document)\n");
        after.Should().Be(before);
    }

    [Fact]
    public async Task Add_DocxWithNineTypeArchiveJson_Archived()
    {
        // Arrange
        var source = await _repo.WriteSourceAsync("sample.docx", ArchiveAddOfficeCommandTests.Sample("sample.docx"));
        var env = new Dictionary<string, string?> { ["ZYGGY_ARCHIVE_CONFIG"] = await ConfigAsync(NineTypes) };

        // Act
        var run = await AddAsync(source, env);

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr + run.Stdout);
        (await _repo.LastCommitSubjectAsync(Ct)).Should().Be("archive add zyggy/roof-offer.docx");
    }
}
