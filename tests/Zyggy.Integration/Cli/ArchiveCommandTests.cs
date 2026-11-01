using System.Text;
using System.Text.RegularExpressions;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Cli;

/// <summary>
/// Spec 41 AC-9..AC-11, AC-13, AC-15, AC-16, AC-25, AC-26: <c>zyggy archive</c> in process from <c>work-laptop</c>'s <c>node.json</c> (no OneDrive
/// folder, DLP on) over its own clone, on the bare repository — a passed file lands as a signed <c>via: bus</c> item in the sender's own
/// <c>items/</c> folder, addressed to central; every refusal leaves the bus as it was and the matched text nowhere.
/// </summary>
public sealed partial class ArchiveCommandTests : IAsyncLifetime
{
    private const string AwsKey = "AKIAABCDEFGHIJKLMNOP";

    private CliHarness _harness = null!;

    public async ValueTask InitializeAsync() => _harness = await CliHarness.CreateAsync(CancellationToken.None);

    public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

    [Fact]
    public async Task Archive_WorkLaptopNoOneDriveFolder_DlpOn_ViaBusItemOnMain_TwoLinesEndingViaBus_Exit0()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string nodeJson = await _harness.AddWorkLaptopAsync(ct);
        byte[] notes = TwoKilobyteNotes();
        string source = Source("restore-notes.md", notes);
        int commits = await _harness.CommitCountOnMainAsync(ct);

        // Act
        var (exit, console) = await _harness.RunAsync(Archive(source), nodeJson, ct: ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        (EnvelopeId id, string path) = Published(console.Stdout);
        path.Should().Be(BusPaths.EnvelopeFile(BusPaths.Items(CliHarness.Acme, CliHarness.WorkLaptop), id)).And.Be($"tenants/acme/nodes/work-laptop/items/{id.Value}.md");
        console.Stdout.Should().Be(
            $"item {id.Value} to central ({path})\n" +
            $"calizr/staging-restore-notes.txt \"Staging restore notes\" (text/markdown, 2.0 KB) — via bus — check: zyggy status --id {id.Value}\n");
        console.Stderr.Should().BeEmpty();
        Model item = await _harness.ParseOnMainAsync(path, ct);
        item.Type.Should().Be(EnvelopeType.Item);
        item.Header.From.Should().Be(CliHarness.WorkLaptop);
        item.Header.To.Should().Be(MachineName.Central);
        item.Item!.Via.Should().Be(ItemRoute.Bus);
        item.Item.DriveName.Should().BeNull();
        item.Item.SourceName.Should().Be("restore-notes.md");
        item.Item.Size.Should().Be(2048);
        ItemBody.TryDecode(item, out byte[]? decoded).Should().BeTrue();
        decoded.Should().Equal(notes);
        (await _harness.CommitCountOnMainAsync(ct)).Should().Be(commits + 1);
        (await _harness.Bus.LastCommitSubjectAsync(ct)).Should().Be($"acme item {id.Value} work-laptop→central");
        (await _harness.PorcelainAsync(_harness.WorkLaptopDir, ct)).Should().BeEmpty();
        SecretSweep.AssertAbsent([console.Stdout, console.Stderr, Encoding.UTF8.GetString(await _harness.Bus.ShowBytesAsync(path, ct))]);
    }

    [Fact]
    public async Task Archive_PushRefusedEveryAttempt_Exit7_LineOneEndsCommittedLocally()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string nodeJson = await _harness.AddWorkLaptopAsync(ct);
        string source = Source("restore-notes.md", TwoKilobyteNotes());
        _harness.Hooks.Override = spec => HookedProcessRunner.SubCommand(spec) == "push"
            ? new Zyggy.Core.Processes.ProcessResult(1, string.Empty, "! [rejected] main -> main (fetch first)", false, false, false, TimeSpan.Zero)
            : null;
        int commits = await _harness.CommitCountOnMainAsync(ct);

        // Act
        var (exit, console) = await _harness.RunAsync(Archive(source), nodeJson, ct: ct);

        // Assert
        exit.Should().Be(7, console.Stderr);
        string[] lines = console.Stdout.Split('\n');
        lines[0].Should().StartWith("item ").And.EndWith(" (committed locally, not yet pushed: the next bus write on this machine pushes it)");
        lines[1].Should().StartWith("calizr/staging-restore-notes.txt \"Staging restore notes\" (text/markdown, 2.0 KB) — via bus — check: zyggy status --id ");
        (await _harness.CommitCountOnMainAsync(ct)).Should().Be(commits);
        (await _harness.Bus.RunGitAsync(_harness.WorkLaptopDir, ["log", "-1", "--format=%s"], ct)).Should().StartWith("acme item ");
        _harness.Hooks.Calls.Count(c => HookedProcessRunner.SubCommand(c) == "push").Should().Be(4);
    }

    [Fact]
    public async Task Archive_SecretLineInFile_Exit5_RefusedNamesPatternAndLine_SecretNowhere()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string nodeJson = await _harness.AddWorkLaptopAsync(ct);
        string source = Source("restore-notes.md", Encoding.UTF8.GetBytes("# Staging restore\n\nkey " + AwsKey + "\n"));
        int commits = await _harness.CommitCountOnMainAsync(ct);

        // Act
        var (exit, console) = await _harness.RunAsync(Archive(source), nodeJson, ct: ct);

        // Assert
        exit.Should().Be(5);
        console.Stderr.Should().Be("zyggy: refused: secret_pattern aws-access-key (line 3)\n");
        console.Stdout.Should().BeEmpty();
        (await _harness.CommitCountOnMainAsync(ct)).Should().Be(commits);
        string history = await _harness.Bus.RunGitAsync(_harness.Bus.BareDir, ["log", "-p", "--all"], ct);
        string clone = string.Concat(Directory.EnumerateFiles(_harness.WorkLaptopDir, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Select(File.ReadAllText));
        new[] { console.Stdout, console.Stderr, history, clone }.Should().NotContain(t => t.Contains(AwsKey, StringComparison.Ordinal));
        SecretSweep.AssertAbsent([console.Stdout, console.Stderr, history, clone]);
    }

    [Fact]
    public async Task Archive_PullFails_Exit6_ItemFailedAtPull_NothingCommitted()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string nodeJson = await _harness.AddWorkLaptopAsync(ct);
        string source = Source("restore-notes.md", TwoKilobyteNotes());
        _harness.Bus.DeleteBare();

        // Act
        var (exit, console) = await _harness.RunAsync(Archive(source), nodeJson, ct: ct);

        // Assert
        exit.Should().Be(6);
        console.Stderr.Should().StartWith("zyggy: item failed at pull:");
        console.Stdout.Should().BeEmpty();
        Directory.Exists(Path.Combine(_harness.WorkLaptopDir, "tenants", "acme", "nodes", "work-laptop", "items")).Should().BeFalse();
        (await _harness.PorcelainAsync(_harness.WorkLaptopDir, ct)).Should().BeEmpty();
        _harness.Hooks.Calls.Should().NotContain(c => HookedProcessRunner.SubCommand(c) == "commit");
    }

    [Fact]
    public async Task Archive_OnCentral_Exit2_UseMemoryArchiveAdd()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string source = Source("restore-notes.md", TwoKilobyteNotes());

        // Act
        var (exit, console) = await _harness.RunAsync(Archive(source), _harness.CentralNodeJson, ct: ct);

        // Assert
        exit.Should().Be(2);
        console.Stderr.Should().Be("zyggy: this machine is central; use zyggy memory archive add\n");
        console.Stdout.Should().BeEmpty();
        _harness.Hooks.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Archive_HooksOff_Exit5_RefusedUnattended_FileNotOpened()
    {
        // Arrange: the file is held exclusively and node.json does not exist — the verb answers before either is touched
        var ct = TestContext.Current.CancellationToken;
        string source = Source("restore-notes.md", TwoKilobyteNotes());
        using var held = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        // Act
        var (exit, console) = await _harness.RunAsync(
            Archive(source),
            _harness.Scratch.File(Path.Combine("absent", "node.json")),
            extraEnvironment: new Dictionary<string, string?> { ["ZYGGY_HOOKS"] = "off" },
            ct: ct);

        // Assert
        exit.Should().Be(5);
        console.Stderr.Should().Be("zyggy: refused: unattended\n");
        console.Stdout.Should().BeEmpty();
        _harness.Hooks.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Archive_FileOverCap_Exit5_TooLarge_NoCommit()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string nodeJson = await _harness.AddWorkLaptopAsync(ct, archiveItemMaxBytes: 1024);
        string source = Source("restore-notes.md", TwoKilobyteNotes());
        int commits = await _harness.CommitCountOnMainAsync(ct);

        // Act
        var (exit, console) = await _harness.RunAsync(Archive(source), nodeJson, ct: ct);

        // Assert
        exit.Should().Be(5);
        console.Stderr.Should().Be("zyggy: refused: too_large (2048 > 1024)\n");
        console.Stdout.Should().BeEmpty();
        (await _harness.CommitCountOnMainAsync(ct)).Should().Be(commits);
        _harness.Hooks.Calls.Should().NotContain(c => HookedProcessRunner.SubCommand(c) == "commit");
    }

    [Fact]
    public async Task Archive_RepeatedOption_Exit2_OneLine()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string nodeJson = await _harness.AddWorkLaptopAsync(ct);
        string source = Source("restore-notes.md", TwoKilobyteNotes());

        // Act
        var (exit, console) = await _harness.RunAsync([.. Archive(source), "--project", "calizr"], nodeJson, ct: ct);

        // Assert
        exit.Should().Be(2);
        console.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().ContainSingle();
        console.Stdout.Should().BeEmpty();
        _harness.Hooks.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Archive_OneDriveFolderRelative_Exit3_ConfigurationErrorNamesKey()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string nodeJson = await _harness.AddWorkLaptopAsync(ct, oneDriveFolder: "OneDrive/Zyggy/archive");
        string source = Source("restore-notes.md", TwoKilobyteNotes());

        // Act
        var (exit, console) = await _harness.RunAsync(Archive(source), nodeJson, ct: ct);

        // Assert
        exit.Should().Be(3);
        console.Stderr.Should().Be("zyggy: configuration error: archive.oneDriveFolder: must be an absolute path\n");
        _harness.Hooks.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Archive_Help_ListedAtTheRoot()
    {
        // Act
        var (exit, console) = await _harness.RunAsync(["--help"], _harness.HomeLaptopNodeJson, ct: TestContext.Current.CancellationToken);

        // Assert
        exit.Should().Be(0, console.Stderr);
        console.Stdout.Should().MatchRegex(@"(?m)^\s+archive\s+Send one file to Central's project archive from a laptop");
    }

    private static string[] Archive(string source) =>
        ["archive", "--project", "calizr", "--name", "Staging restore notes", "--description", "How the staging database was restored", "--file", source];

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

    private string Source(string name, byte[] bytes)
    {
        string path = _harness.Scratch.File(Path.Combine("sources", name));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
