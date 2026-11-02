using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Processes;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Cli;
using Zyggy.Integration.Infrastructure;
using Zyggy.Integration.Memory;
using Zyggy.Integration.Node;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Gates;

/// <summary>
/// Spec 41 AC-45, the gate of "archive from any node" (plan 41 Step 15): the whole round trip through the real components, tenant
/// <c>acme</c>. <c>home-laptop</c> (OneDrive folder configured: a temp folder the test mirrors into the stub OneDrive, standing in for the
/// laptop's sync client) archives a Markdown and a PNG item through the OneDrive route; <c>work-laptop</c> (none configured, DLP on)
/// archives one through the bus route — each an in-process <c>zyggy archive</c> on its own bus checkout. Central's production host
/// (<see cref="NodeHostHarness"/>, the stub Graph handler, never the real Graph) files all three. A tampered item is never filed; an
/// oversized file is refused on the laptop and reaches neither the folder nor the bus. Never <c>claude</c>, never GitHub.
/// </summary>
public sealed partial class P1_ArchiveFromNode : IAsyncLifetime
{
    private static readonly MachineName Central = NodeHostHarness.Central;

    private static readonly MachineName HomeLaptop = NodeHostHarness.HomeLaptop;

    private static readonly MachineName WorkLaptop = MachineName.Parse("work-laptop");

    private static readonly byte[] Notes = "# Staging restore\n\nRestore the staging database from Sunday's snapshot.\n"u8.ToArray();

    private static readonly byte[] Checklist = "Restore checklist\n\n1. Stop the app.\n2. Restore the snapshot.\n3. Start the app.\n"u8.ToArray();

    private readonly HookedProcessRunner _laptopHooks = TestHost.Hooks();

    private NodeHostHarness _central = null!;

    private PickupArchiveMemory _memory = null!;

    private string _homeClone = null!;

    private string _workClone = null!;

    private Dictionary<string, string?> _closedHealth = null!;

    private string OneDriveFolder => _central.Scratch.File("onedrive");

    public async ValueTask InitializeAsync()
    {
        var ct = CancellationToken.None;
        _central = await NodeHostHarness.CreateAsync(ct);
        _memory = new PickupArchiveMemory(m365: true);
        await _memory.InitializeAsync();
        _central.WriteNodeJson(machine: Central, activeSeconds: 1, idleSeconds: 60);
        _central.Environment = _memory.Env();
        _homeClone = await _central.Bus.CloneAsync("home", ct);
        _workClone = await _central.Bus.CloneAsync("work", ct);
        Directory.CreateDirectory(OneDriveFolder);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        _closedHealth = new() { ["ZYGGY_HEALTH_URL"] = $"http://127.0.0.1:{port}/health" };
    }

    public async ValueTask DisposeAsync()
    {
        await _memory.DisposeAsync();
        await _central.DisposeAsync();
    }

    [Fact]
    public async Task Gate_HomeLaptopOneDriveMarkdownAndPng_WorkLaptopBus_CentralFilesAllThree_ThreeDoneReports_ItemsRemoved_MemoryPushed()
    {
        // Arrange: the two laptops archive; the stand-in sync client mirrors home-laptop's folder into OneDrive.
        var ct = TestContext.Current.CancellationToken;
        string home = HomeLaptopNodeJson();
        string work = WorkLaptopNodeJson();
        EnvelopeId markdown = await ArchiveAsync(home, "restore-notes.md", Notes, "staging-restore-notes", "Staging restore notes", "— via OneDrive —", ct);
        EnvelopeId png = await ArchiveAsync(home, "restore-screen.png", ArchiveAddCommandTests.Png(), "restore-screen", "Restore screen", "— via OneDrive —", ct);
        EnvelopeId bus = await ArchiveAsync(work, "restore-checklist.txt", Checklist, "restore-checklist", "Restore checklist", "— via bus —", ct);
        MirrorIntoOneDrive();
        string[] items =
        [
            BusPaths.EnvelopeFile(BusPaths.Items(NodeHostHarness.Acme, HomeLaptop), markdown),
            BusPaths.EnvelopeFile(BusPaths.Items(NodeHostHarness.Acme, HomeLaptop), png),
            BusPaths.EnvelopeFile(BusPaths.Items(NodeHostHarness.Acme, WorkLaptop), bus),
        ];
        int memoryCommits = await MemoryCommitCountAsync(ct);

        // Act: Central's production host runs until every item has left the bus.
        int exit = await _central.RunUntilAsync(
            async () => await AllGoneAsync(items, ct) && (await CentralReportsAsync(ct)).Count == 3,
            TimeSpan.FromSeconds(60),
            ct,
            s => s.AddSingleton(_memory.DriveOptions));

        // Assert: memory — three items and sidecars pushed, three commits, three [stated] index lines.
        exit.Should().Be(0, string.Join('\n', _central.LogLines));
        (await _memory.Repo.ShowBytesAsync("acme/alice/archive/calizr/staging-restore-notes.txt", ct)).Should().Equal(Notes);
        (await _memory.Repo.ShowBytesAsync("acme/alice/archive/calizr/restore-screen.png", ct)).Should().Equal(ArchiveAddCommandTests.Png());
        (await _memory.Repo.ShowBytesAsync("acme/alice/archive/calizr/restore-checklist.txt", ct)).Should().Equal(Checklist);
        foreach (string sidecar in new[] { "staging-restore-notes", "restore-screen", "restore-checklist" })
        {
            (await _memory.Repo.ShowAsync($"acme/alice/archive/calizr/{sidecar}.md", ct)).Should().Contain("project: calizr");
        }

        (await MemoryCommitCountAsync(ct)).Should().Be(memoryCommits + 3);
        (await _memory.Repo.GitAsync(_memory.Repo.BareDir, ["log", "--format=%s", "main"], ct)).Split('\n')
            .Where(s => s.StartsWith("archive add calizr/", StringComparison.Ordinal)).Should().BeEquivalentTo(
                "archive add calizr/staging-restore-notes.txt", "archive add calizr/restore-screen.png", "archive add calizr/restore-checklist.txt");
        IndexLines().Should().HaveCount(3);

        // Assert: bus — items/ empty on main, three verified done reports from central answering the three ids.
        (await ItemsOnMainAsync(HomeLaptop, ct)).Should().BeEmpty();
        (await ItemsOnMainAsync(WorkLaptop, ct)).Should().BeEmpty();
        IReadOnlyList<Model> reports = await CentralReportsAsync(ct);
        reports.Should().HaveCount(3).And.OnlyContain(r => r.Report!.Status == ReportStatus.Done && r.Header.From == Central);
        reports.Select(r => r.Header.InReplyTo).Should().BeEquivalentTo(new[] { markdown, png, bus });
        reports.Single(r => r.Header.InReplyTo == markdown).Header.To.Should().Be(HomeLaptop);
        reports.Single(r => r.Header.InReplyTo == bus).Header.To.Should().Be(WorkLaptop);
        reports.Single(r => r.Header.InReplyTo == markdown).BodyText.Should()
            .Contain("onedrive: Zyggy/archive/calizr/staging-restore-notes.txt (adopted by the next offload)");
        reports.Select(r => r.BodyText.Split('\n')[2]).Should().BeEquivalentTo(IndexLines());

        // Assert: no Graph write, no model run, nothing of the files in the node's log.
        _memory.Drive.Puts.Should().Be(0);
        _memory.Graph.Requests.Should().NotContain(r => r.Method != HttpMethod.Get && !(r.Method == HttpMethod.Post && r.Uri.Host == "login.microsoftonline.com"));
        _central.Hooks.Calls.Concat(_laptopHooks.Calls).Should().NotContain(
            c => Path.GetFileName(c.FileName).Contains("claude", StringComparison.OrdinalIgnoreCase) && !(c.Arguments.Count == 2 && c.Arguments[0] == "auth" && c.Arguments[1] == "status"),
            "the only claude call is the node's login probe; nothing runs a model");
        _central.LogLines.Should().NotContain(l => l.Contains("Sunday's snapshot", StringComparison.Ordinal) || l.Contains("Stop the app", StringComparison.Ordinal));

        // Assert: zyggy status --id on each sender (its checkout pulled, as its node would) says reported by central.
        await PullAsync(_homeClone, ct);
        await PullAsync(_workClone, ct);
        foreach ((string nodeJson, EnvelopeId id) in new[] { (home, markdown), (home, png), (work, bus) })
        {
            var (statusExit, console) = await RunZyggyAsync(["status", "--id", id.Value], nodeJson, _closedHealth, ct);
            statusExit.Should().Be(0, console.Stderr);
            string[] lines = console.Stdout.Split('\n');
            lines[0].Should().Be($"item    {id.Value}  (file gone)");
            lines[1].Should().Be("state   reported by central");
        }
    }

    [Fact]
    public async Task Gate_TamperedItem_NeverFiled_LeftOnMain_NoReport()
    {
        // Arrange: work-laptop's item edited on the bus after signing (the name changed), next to a clean one from home-laptop.
        var ct = TestContext.Current.CancellationToken;
        EnvelopeId tampered = await ArchiveAsync(WorkLaptopNodeJson(), "restore-checklist.txt", Checklist, "restore-checklist", "Restore checklist", "— via bus —", ct);
        string tamperedPath = BusPaths.EnvelopeFile(BusPaths.Items(NodeHostHarness.Acme, WorkLaptop), tampered);
        string text = Encoding.UTF8.GetString(await _central.Bus.ShowBytesAsync(tamperedPath, ct));
        await _central.Bus.SeedRawFileAsync(tamperedPath, Encoding.UTF8.GetBytes(text.Replace("name: Restore checklist", "name: Restore checklisd", StringComparison.Ordinal)), "edit by hand", ct);
        EnvelopeId clean = await ArchiveAsync(
            HomeLaptopNodeJson(), "restore-notes.md", Notes, "staging-restore-notes", "Staging restore notes", "— via bus —", ct, viaBus: true);
        string cleanPath = BusPaths.EnvelopeFile(BusPaths.Items(NodeHostHarness.Acme, HomeLaptop), clean);
        string memoryHead = await _memory.Repo.GitAsync(_memory.Repo.BareDir, ["rev-parse", "main"], ct);

        // Act: Central runs until the clean item is filed and the tampered one has been looked at (its warning).
        int exit = await _central.RunUntilAsync(
            async () => await AllGoneAsync([cleanPath], ct)
                && _central.LogLines.Any(l => l.Contains($"pickup: left {tamperedPath}: invalid_signature (sig)", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(60),
            ct,
            s => s.AddSingleton(_memory.DriveOptions));

        // Assert
        exit.Should().Be(0, string.Join('\n', _central.LogLines));
        (await _central.Bus.ExistsOnMainAsync(tamperedPath, ct)).Should().BeTrue();
        (await CentralReportsAsync(ct)).Should().ContainSingle().Which.Header.InReplyTo.Should().Be(clean);
        (await _memory.Repo.GitAsync(_memory.Repo.BareDir, ["log", "--format=%s", $"{memoryHead}..main"], ct)).Should().Be("archive add calizr/staging-restore-notes.txt");
        File.Exists(Path.Combine(_memory.Repo.PrincipalDir, "archive", "calizr", "restore-checklist.txt")).Should().BeFalse();
        _central.LogLines.Count(l => l.Contains($"pickup: left {tamperedPath}", StringComparison.Ordinal)).Should().Be(1);
    }

    [Fact]
    public async Task Gate_OversizedFileOnLaptop_Exit5TooLarge_FolderEmpty_NoBusCommit()
    {
        // Arrange: home-laptop's cap at 1,024 bytes, its OneDrive folder configured and present.
        var ct = TestContext.Current.CancellationToken;
        string nodeJson = HomeLaptopNodeJson(archiveItemMaxBytes: 1024);
        string source = Source("large-notes.md", Encoding.UTF8.GetBytes("# Large notes\n\n" + string.Concat(Enumerable.Repeat("Restore the staging database from the Sunday snapshot.\n", 40))));
        string head = await _central.Bus.HeadShaAsync(ct);

        // Act
        var (exit, console) = await RunZyggyAsync(Archive(source, "large-notes", "Large notes"), nodeJson, null, ct);

        // Assert
        exit.Should().Be(5);
        console.Stderr.Should().MatchRegex(@"^zyggy: refused: too_large \(\d+ > 1024\)\n$");
        console.Stdout.Should().BeEmpty();
        Directory.EnumerateFileSystemEntries(OneDriveFolder, "*", SearchOption.AllDirectories).Should().BeEmpty();
        (await _central.Bus.HeadShaAsync(ct)).Should().Be(head);
        _laptopHooks.Calls.Should().NotContain(c => HookedProcessRunner.SubCommand(c) == "commit");
    }

    private static string[] Archive(string source, string slug, string name) =>
        ["archive", "--project", "calizr", "--name", name, "--description", "How the staging database was restored", "--file", source, "--slug", slug];

    private Task<string> PullAsync(string clone, CancellationToken ct) => _central.Bus.RunGitAsync(clone, ["pull", "-q", "--rebase", "origin", "main"], ct);

    private async Task<EnvelopeId> ArchiveAsync(string nodeJson, string sourceName, byte[] bytes, string slug, string name, string route, CancellationToken ct, bool viaBus = false)
    {
        string source = Source(sourceName, bytes);
        string[] args = viaBus ? [.. Archive(source, slug, name), "--via", "bus"] : Archive(source, slug, name);
        var (exit, console) = await RunZyggyAsync(args, nodeJson, null, ct);
        exit.Should().Be(0, console.Stderr);
        console.Stdout.Should().Contain(route);
        Match line = PublishedLine().Match(console.Stdout);
        line.Success.Should().BeTrue(console.Stdout);
        return EnvelopeId.Parse(line.Groups["id"].Value);
    }

    private Task<(int Exit, VerbConsole Console)> RunZyggyAsync(string[] args, string nodeJson, IReadOnlyDictionary<string, string?>? extra, CancellationToken ct)
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ZYGGY_NODE_JSON"] = nodeJson,
            ["ZYGGY_SECRET_PATTERNS"] = CliHarness.PatternsPath,
        };
        foreach (var (key, value) in extra ?? new Dictionary<string, string?>())
        {
            environment[key] = value;
        }

        return ZyggyCliInProcess.RunAsync(
            args,
            environment,
            s =>
            {
                s.Configure<GitHubBusOptions>(o =>
                {
                    o.HandlerFactory = () => _central.Head;
                    o.GitEnvironment = _central.Bus.GitEnvironment;
                    o.Jitter = () => 0;
                });
                s.AddSingleton<IProcessRunner>(_laptopHooks);
            },
            null,
            ct);
    }

    private string HomeLaptopNodeJson(long? archiveItemMaxBytes = null) => LaptopNodeJson(HomeLaptop, _homeClone, archiveItemMaxBytes, OneDriveFolder, dlp: false);

    private string WorkLaptopNodeJson() => LaptopNodeJson(WorkLaptop, _workClone, null, null, dlp: true);

    private string LaptopNodeJson(MachineName machine, string clone, long? archiveItemMaxBytes, string? oneDriveFolder, bool dlp) =>
        NodeJson.Write(
            _central.Scratch.File("config-" + machine.Value),
            NodeHostHarness.Acme,
            machine,
            TestKeys.Acme1,
            busLocalPath: clone,
            remote: TestHost.Remote.HttpsUrl!.ToString(),
            secretsRoot: _central.SecretsRoot,
            claudePath: FakeClaude.ExecutablePath,
            nodeDir: _central.Scratch.File("node-" + machine.Value),
            archiveItemMaxBytes: archiveItemMaxBytes,
            oneDriveFolder: oneDriveFolder,
            dlpEnabled: dlp);

    private string Source(string name, byte[] bytes)
    {
        string path = _central.Scratch.File(Path.Combine("sources", name));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>The stand-in for home-laptop's OneDrive client: every project folder and file under the laptop's folder, uploaded to <c>Zyggy/archive</c>.</summary>
    private void MirrorIntoOneDrive()
    {
        foreach (string project in Directory.EnumerateDirectories(OneDriveFolder))
        {
            string folderId = _memory.Drive.Add(StubOneDrive.RootId, Path.GetFileName(project));
            foreach (string file in Directory.EnumerateFiles(project))
            {
                _memory.Drive.Add(folderId, Path.GetFileName(file), File.ReadAllBytes(file));
            }
        }
    }

    private async Task<bool> AllGoneAsync(IEnumerable<string> paths, CancellationToken ct)
    {
        foreach (string path in paths)
        {
            if (await _central.Bus.ExistsOnMainAsync(path, ct))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<IReadOnlyList<string>> ItemsOnMainAsync(MachineName machine, CancellationToken ct)
    {
        string output = await _central.Bus.RunGitAsync(_central.Bus.BareDir, ["ls-tree", "--name-only", "main", BusPaths.Items(NodeHostHarness.Acme, machine) + "/"], ct);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(p => p.EndsWith(".md", StringComparison.Ordinal)).ToList();
    }

    private async Task<IReadOnlyList<Model>> CentralReportsAsync(CancellationToken ct)
    {
        string output = await _central.Bus.RunGitAsync(_central.Bus.BareDir, ["ls-tree", "--name-only", "main", BusPaths.Reports(NodeHostHarness.Acme, Central) + "/"], ct);
        var reports = new List<Model>();
        foreach (string path in output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(p => p.EndsWith(".md", StringComparison.Ordinal)))
        {
            reports.Add(await _central.ReportAsync(path, ct));
        }

        return reports;
    }

    private async Task<int> MemoryCommitCountAsync(CancellationToken ct) =>
        int.Parse(await _memory.Repo.GitAsync(_memory.Repo.BareDir, ["rev-list", "--count", "main"], ct), System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The archive's <c>[stated]</c> index lines in the memory clone's inbox (the dream files them).</summary>
    private List<string> IndexLines() =>
        Directory.EnumerateFiles(_memory.Paths.InboxDirectory, "remember-*.md")
            .SelectMany(File.ReadAllLines)
            .Where(l => l.StartsWith("- [stated] ", StringComparison.Ordinal) && l.Contains("(project:calizr)", StringComparison.Ordinal))
            .ToList();

    [GeneratedRegex(@"^item (?<id>[0-9A-HJKMNP-TV-Z]{26}) to central \((?<path>\S+)\)")]
    private static partial Regex PublishedLine();
}
