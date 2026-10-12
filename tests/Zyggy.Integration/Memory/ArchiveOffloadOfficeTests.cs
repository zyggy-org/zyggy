using System.Text;

using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Memory;

/// <summary>
/// Plan 39 Step 5 (spec 39 AC-18 integration): with no offload code change, the in-process offload over the stub OneDrive uploads an
/// indexed <c>roof-offer.docx</c> as <c>application/octet-stream</c> with <c>conflictBehavior=fail</c>, verifies size and QuickXorHash,
/// writes the offload keys into the sidecar (byte-equal to the golden), removes the item in one <c>archive offload</c> commit and
/// <c>list</c> says <c>offloaded</c>; a failed upload leaves the docx in git.
/// </summary>
public sealed class ArchiveOffloadOfficeTests : IAsyncLifetime
{
    private const string Item = "acme/alice/archive/zyggy/roof-offer.docx";

    private const string Sidecar = "acme/alice/archive/zyggy/roof-offer.md";

    // From quickxor-vectors.tsv (an independent implementation), never from the code under test.
    private const string DocxQuickXor = "KUrXgAMRB/tsuE7mg8tmakHsOVo=";

    private const string DocxSha256 = "fa0d4998a5b647d23edf5ec41eaa6a1192341017c46d5f465a4208fc5345e0ca";

    private readonly OffloadHarness _h = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Golden(params string[] parts) => File.ReadAllBytes(Path.Combine([AppContext.BaseDirectory, "golden", .. parts]));

    public async ValueTask InitializeAsync()
    {
        // The harness seeds two indexed 37 items; this slice replaces them by the indexed docx: item, sidecar golden, label line.
        await _h.InitializeAsync();
        var clone = _h.Repo.CloneDir;
        await _h.Repo.GitAsync(clone, ["rm", "-q", "--", "acme/alice/archive/zyggy/quote-2026.txt", "acme/alice/archive/zyggy/quote-2026.md", "acme/alice/archive/zyggy/screenshot.png", "acme/alice/archive/zyggy/screenshot.md"], Ct);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(clone, Item))!);
        await File.WriteAllBytesAsync(Path.Combine(clone, Item), Golden("office", "sample.docx"), Ct);
        await File.WriteAllBytesAsync(Path.Combine(clone, Sidecar), Golden("archive", "sidecar-docx.md"), Ct);
        await File.WriteAllBytesAsync(
            Path.Combine(_h.Repo.PrincipalDir, "business", "areas", "zyggy.md"),
            new UTF8Encoding(false).GetBytes(
                "---\nname: zyggy\ndescription: Zyggy, the agent platform\nupdated: 2026-09-30\n---\n"
                + "- [stated] 2026-09-30 (project:zyggy): Archived \"Roof offer\" (Word document, 1.3 KB) at archive/zyggy/roof-offer.docx — Contractor offer for the roof repair\n"),
            Ct);
        await _h.Repo.GitAsync(clone, ["add", "--all", "--", "acme/alice/archive", "acme/alice/business"], Ct);
        await _h.Repo.GitAsync(clone, ["commit", "-q", "-m", "seed office"], Ct);
        await _h.Repo.GitAsync(clone, ["push", "-q", "origin", "HEAD:main"], Ct);
        _h.Drive.NextIds.Enqueue("01PROJZYGGY");
        _h.Drive.NextIds.Enqueue("01ITEMOFFER");
    }

    public ValueTask DisposeAsync() => _h.DisposeAsync();

    [Fact]
    public async Task Offload_IndexedDocx_UploadedAsDocxOctetStreamConflictFail()
    {
        // Act
        var (exit, console) = await _h.RunInProcessAsync();

        // Assert
        exit.Should().Be(0, console.Stderr);
        var put = _h.Graph.Requests.Single(r => r.Method == HttpMethod.Put);
        Uri.UnescapeDataString(put.Uri.ToString()).Should().EndWith("/items/01PROJZYGGY:/roof-offer.docx:/content?@microsoft.graph.conflictBehavior=fail");
        put.Headers["Content-Type"].Should().Be("application/octet-stream");
        put.Bytes.Should().Equal(Golden("office", "sample.docx"));
        _h.Drive.File("01PROJZYGGY", "roof-offer.docx").Should().Equal(Golden("office", "sample.docx"));
    }

    [Fact]
    public async Task Offload_IndexedDocx_VerifiedSizeAndQuickXorThenOneCommitItemGone()
    {
        // Arrange
        var before = await _h.Repo.CommitCountAsync(Ct);

        // Act
        var (exit, console) = await _h.RunInProcessAsync();

        // Assert
        exit.Should().Be(0, console.Stderr);
        (await _h.Repo.CommitCountAsync(Ct)).Should().Be(before + 1);
        (await _h.Repo.LastCommitSubjectAsync(Ct)).Should().Be("archive offload zyggy/roof-offer.docx");
        (await _h.Repo.LastCommitBodyAsync(Ct)).Should()
            .StartWith("drive_item_id: 01ITEMOFFER")
            .And.Contain($"; sha256: {DocxSha256}; quickxor: {DocxQuickXor}; size: 1289 bytes")
            .And.EndWith("Zyggy-Tool: memory archive offload");
        (await _h.Repo.GitAsync(_h.Repo.BareDir, ["show", "--name-status", "--format=", "main"], Ct)).Split('\n')
            .Should().Equal($"D\t{Item}", $"M\t{Sidecar}");
        (await _h.Repo.GitAsync(_h.Repo.BareDir, ["ls-tree", "-r", "--name-only", "main"], Ct)).Split('\n').Should().NotContain(Item).And.Contain(Sidecar);
    }

    [Fact]
    public async Task Offload_IndexedDocx_SidecarHasOffloadKeysMediaTypeUnchanged()
    {
        // Act
        var (exit, console) = await _h.RunInProcessAsync();

        // Assert
        exit.Should().Be(0, console.Stderr);
        (await _h.Repo.ShowBytesAsync(Sidecar, Ct)).Should().Equal(Golden("archive", "offload", "sidecar-docx-offloaded.md"));
    }

    [Fact]
    public async Task Offload_ThenList_ShowsOffloadedWithDrivePath()
    {
        // Arrange
        await _h.RunInProcessAsync();

        // Act
        var run = await ZyggyCli.RunAsync(["memory", "archive", "list"], _h.Env(), null, _h.Repo.RootDir, Ct);

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        run.Stdout.Should().Be(
            "archive/zyggy/roof-offer.docx  application/vnd.openxmlformats-officedocument.wordprocessingml.document  1.3 KB  offloaded → Zyggy/archive/zyggy/roof-offer.docx  — Contractor offer for the roof repair\n");
    }

    [Fact]
    public async Task Offload_UploadFails_DocxStaysInGit()
    {
        // Arrange: the project folder exists, so the next write is the upload, answered 500.
        _h.Drive.Add(StubOneDrive.RootId, "zyggy");
        _h.Drive.RefuseNextWith(500);
        var fingerprint = await _h.Repo.TreeFingerprintAsync(Ct);
        var remote = await _h.Repo.GitAsync(_h.Repo.BareDir, ["rev-parse", "main"], Ct);

        // Act
        var (exit, console) = await _h.RunInProcessAsync();

        // Assert
        exit.Should().Be(5);
        console.Stderr.Should().StartWith("offload: zyggy/roof-offer.docx: server_error");
        (await _h.Repo.TreeFingerprintAsync(Ct)).Should().Be(fingerprint);
        (await _h.Repo.GitAsync(_h.Repo.BareDir, ["rev-parse", "main"], Ct)).Should().Be(remote);
        (await _h.Repo.ShowBytesAsync(Item, Ct)).Should().Equal(Golden("office", "sample.docx"));
    }
}
