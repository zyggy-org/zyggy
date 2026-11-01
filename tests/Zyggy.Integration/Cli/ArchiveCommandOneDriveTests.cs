using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

using Zyggy.Core.Envelope;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Cli;

/// <summary>
/// Spec 41 AC-15, AC-18, AC-21, AC-23, AC-24: <c>zyggy archive</c> in process from <c>home-laptop</c>'s <c>node.json</c> with
/// <c>archive.oneDriveFolder</c> = a temp folder standing in for the synced OneDrive archive folder, on the bare repository — the bytes are
/// placed under <c>&lt;folder&gt;/calizr/</c> and a <c>via: onedrive</c> item is published; a missing folder is exit 6 with the
/// <c>--via bus</c> hint and nothing on the bus; a retried call reuses the identical file; a refusal leaves the folder empty.
/// </summary>
public sealed partial class ArchiveCommandOneDriveTests : IAsyncLifetime
{
    private const string AwsKey = "AKIAABCDEFGHIJKLMNOP";

    private CliHarness _harness = null!;

    private string Folder => _harness.Scratch.File("onedrive");

    private string Target => Path.Combine(Folder, "calizr", "staging-restore-notes.txt");

    public async ValueTask InitializeAsync() => _harness = await CliHarness.CreateAsync(CancellationToken.None);

    public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

    [Fact]
    public async Task Archive_HomeLaptopOneDriveFolder_FileInFolder_ViaOneDriveItemOnMain_ExactTwoLines_Exit0()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Folder);
        string nodeJson = HomeLaptopWithFolder(Folder);
        byte[] notes = TwoKilobyteNotes();
        string source = Source("restore-notes.md", notes);
        int commits = await _harness.CommitCountOnMainAsync(ct);

        // Act
        var (exit, console) = await _harness.RunAsync(Archive(source), nodeJson, ct: ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        (EnvelopeId id, string path) = Published(console.Stdout);
        console.Stdout.Should().Be(
            $"item {id.Value} to central (tenants/acme/nodes/home-laptop/items/{id.Value}.md)\n" +
            $"calizr/staging-restore-notes.txt \"Staging restore notes\" (text/markdown, 2.0 KB) — via OneDrive — check: zyggy status --id {id.Value}\n");
        console.Stderr.Should().BeEmpty();
        SHA256.HashData(File.ReadAllBytes(Target)).Should().Equal(SHA256.HashData(notes));
        Directory.EnumerateFileSystemEntries(Folder, "*", SearchOption.AllDirectories).Should().BeEquivalentTo([Path.Combine(Folder, "calizr"), Target]);
        Model item = await _harness.ParseOnMainAsync(path, ct);
        item.Header.From.Should().Be(CliHarness.HomeLaptop);
        item.Item!.Via.Should().Be(ItemRoute.OneDrive);
        item.Item.DriveName.Should().Be("staging-restore-notes.txt");
        item.Item.Size.Should().Be(2048);
        item.Item.Sha256.Should().Be(Convert.ToHexStringLower(SHA256.HashData(notes)));
        item.Body.Length.Should().Be(0);
        (await _harness.CommitCountOnMainAsync(ct)).Should().Be(commits + 1);
        (await _harness.Bus.LastCommitSubjectAsync(ct)).Should().Be($"acme item {id.Value} home-laptop→central");
        (await _harness.PorcelainAsync(_harness.Bus.CloneDir, ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Archive_OneDriveFolderMissing_Exit6_HintViaBus_NothingOnBus()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string nodeJson = HomeLaptopWithFolder(Folder);
        string source = Source("restore-notes.md", TwoKilobyteNotes());
        int commits = await _harness.CommitCountOnMainAsync(ct);

        // Act
        var (exit, console) = await _harness.RunAsync(Archive(source), nodeJson, ct: ct);

        // Assert
        exit.Should().Be(6);
        console.Stderr.Should().Be($"zyggy: OneDrive folder {Slashes(Folder)} is not available (missing): fix the folder, or archive through the bus with --via bus\n");
        console.Stdout.Should().BeEmpty();
        Directory.Exists(Folder).Should().BeFalse();
        (await _harness.CommitCountOnMainAsync(ct)).Should().Be(commits);
        Directory.Exists(Path.Combine(_harness.Bus.CloneDir, "tenants", "acme", "nodes", "home-laptop", "items")).Should().BeFalse();
        _harness.Hooks.Calls.Should().NotContain(c => HookedProcessRunner.SubCommand(c) == "commit");
    }

    [Fact]
    public async Task Archive_OneDriveFolderMissing_WithViaBus_ViaBusItem_FolderUntouched()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string nodeJson = HomeLaptopWithFolder(Folder);
        byte[] notes = TwoKilobyteNotes();
        string source = Source("restore-notes.md", notes);

        // Act
        var (exit, console) = await _harness.RunAsync([.. Archive(source), "--via", "bus"], nodeJson, ct: ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        (EnvelopeId id, string path) = Published(console.Stdout);
        console.Stdout.Split('\n')[1].Should().Be($"calizr/staging-restore-notes.txt \"Staging restore notes\" (text/markdown, 2.0 KB) — via bus — check: zyggy status --id {id.Value}");
        Model item = await _harness.ParseOnMainAsync(path, ct);
        item.Item!.Via.Should().Be(ItemRoute.Bus);
        ItemBody.TryDecode(item, out byte[]? decoded).Should().BeTrue();
        decoded.Should().Equal(notes);
        Directory.Exists(Folder).Should().BeFalse();
    }

    [Fact]
    public async Task Archive_PushRefusedAfterWrite_Exit7_FileStays_RerunSameCommand_IdenticalReused_ExactlyOneMoreEnvelope()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Folder);
        string nodeJson = HomeLaptopWithFolder(Folder);
        byte[] notes = TwoKilobyteNotes();
        string source = Source("restore-notes.md", notes);
        _harness.Hooks.Override = spec => HookedProcessRunner.SubCommand(spec) == "push"
            ? new Zyggy.Core.Processes.ProcessResult(1, string.Empty, "! [rejected] main -> main (fetch first)", false, false, false, TimeSpan.Zero)
            : null;
        var (firstExit, first) = await _harness.RunAsync(Archive(source), nodeJson, ct: ct);
        DateTime placed = File.GetLastWriteTimeUtc(Target);
        int localCommits = await LocalCommitCountAsync(ct);
        _harness.Hooks.Override = null;

        // Act
        var (exit, console) = await _harness.RunAsync(Archive(source), nodeJson, ct: ct);

        // Assert
        firstExit.Should().Be(7, first.Stderr);
        first.Stdout.Should().Contain("(committed locally, not yet pushed: the next bus write on this machine pushes it)");
        exit.Should().Be(0, console.Stderr);
        (EnvelopeId secondId, string secondPath) = Published(console.Stdout);
        console.Stdout.Should().EndWith($"— via OneDrive — check: zyggy status --id {secondId.Value}\n");
        (await LocalCommitCountAsync(ct)).Should().Be(localCommits + 1, "the re-run publishes exactly one envelope");
        Directory.EnumerateFiles(Folder, "*", SearchOption.AllDirectories).Should().Equal(Target);
        File.GetLastWriteTimeUtc(Target).Should().Be(placed, "identical bytes are reused, not rewritten");
        File.ReadAllBytes(Target).Should().Equal(notes);
        (await _harness.ParseOnMainAsync(secondPath, ct)).Item!.Via.Should().Be(ItemRoute.OneDrive);
    }

    [Fact]
    public async Task Archive_SecretInName_Exit5_FolderEmpty_BareRepoUnchanged()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Folder);
        string nodeJson = HomeLaptopWithFolder(Folder);
        string source = Source("restore-notes.md", TwoKilobyteNotes());
        string[] args = ["archive", "--project", "calizr", "--name", "Staging key " + AwsKey, "--description", "How the staging database was restored", "--file", source, "--slug", "staging-restore-notes"];
        int commits = await _harness.CommitCountOnMainAsync(ct);

        // Act
        var (exit, console) = await _harness.RunAsync(args, nodeJson, ct: ct);

        // Assert
        exit.Should().Be(5);
        console.Stderr.Should().Be("zyggy: refused: secret_pattern aws-access-key (name)\n");
        console.Stdout.Should().BeEmpty();
        Directory.EnumerateFileSystemEntries(Folder).Should().BeEmpty();
        (await _harness.CommitCountOnMainAsync(ct)).Should().Be(commits);
        string history = await _harness.Bus.RunGitAsync(_harness.Bus.BareDir, ["log", "-p", "--all"], ct);
        new[] { console.Stdout, console.Stderr, history }.Should().NotContain(t => t.Contains(AwsKey, StringComparison.Ordinal));
        SecretSweep.AssertAbsent([console.Stdout, console.Stderr, history]);
    }

    private static string[] Archive(string source) =>
        ["archive", "--project", "calizr", "--name", "Staging restore notes", "--description", "How the staging database was restored", "--file", source];

    private static string Slashes(string path) => path.Replace('\\', '/');

    /// <summary>A 2,048-byte Markdown file (AC-18's "2 KB"): a heading and numbered steps, padded with one last line.</summary>
    private static byte[] TwoKilobyteNotes()
    {
        var text = new StringBuilder("# Staging restore notes\n\n");
        for (int step = 1; text.Length < 1900; step++)
        {
            text.Append("Step ").Append(step).Append(": restore the staging database from the Sunday snapshot.\n");
        }

        text.Append(new string('.', 2047 - text.Length)).Append('\n');
        return Encoding.UTF8.GetBytes(text.ToString());
    }

    private static (EnvelopeId Id, string Path) Published(string stdout)
    {
        Match line = PublishedLine().Match(stdout);
        line.Success.Should().BeTrue(stdout);
        return (EnvelopeId.Parse(line.Groups["id"].Value), line.Groups["path"].Value);
    }

    [GeneratedRegex(@"^item (?<id>[0-9A-HJKMNP-TV-Z]{26}) to central \((?<path>\S+)\)")]
    private static partial Regex PublishedLine();

    /// <summary>Rewrites <c>home-laptop</c>'s <c>node.json</c> (same file, same keys) with <c>archive.oneDriveFolder</c> set.</summary>
    private string HomeLaptopWithFolder(string folder) =>
        NodeJson.Write(
            _harness.Scratch.File("config-home-laptop"),
            CliHarness.Acme,
            CliHarness.HomeLaptop,
            TestKeys.Acme1,
            busLocalPath: _harness.Bus.CloneDir,
            remote: TestHost.Remote.HttpsUrl!.ToString(),
            secretsRoot: _harness.SecretsRoot,
            claudePath: FakeClaude.ExecutablePath,
            nodeDir: _harness.NodeDirOf("home-laptop"),
            oneDriveFolder: folder);

    private async Task<int> LocalCommitCountAsync(CancellationToken ct) =>
        int.Parse(await _harness.Bus.RunGitAsync(_harness.Bus.CloneDir, ["rev-list", "--count", "HEAD"], ct), System.Globalization.CultureInfo.InvariantCulture);

    private string Source(string name, byte[] bytes)
    {
        string path = _harness.Scratch.File(Path.Combine("sources", name));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
