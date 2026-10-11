using System.Security.Cryptography;
using System.Text;

using Zyggy.Core.Git;
using Zyggy.Core.M365.Graph;
using Zyggy.Core.M365.Guard;
using Zyggy.Core.Memory;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Core.Tests.M365;

namespace Zyggy.Core.Tests.Memory;

/// <summary>
/// One offload run in process (plan 38 Steps 5–6): a temporary memory tree with indexed items, git recorded, the real Graph reader and
/// writer over the stubbed handler and an in-memory OneDrive, a fake clock at 2026-10-01T03:30:00Z (05:30 in Brussels).
/// </summary>
internal sealed class OffloadFixture : IDisposable
{
    public const string TextItem = "Quote for the Zyggy roof repair.\nTotal 1 234,00 EUR, valid 30 days.\n";

    public static readonly DateTimeOffset Now = new(2026, 10, 1, 3, 30, 0, TimeSpan.Zero);

    private readonly List<string> _projectLines = [];

    public OffloadFixture(bool withRoot = true)
    {
        Drive = new StubOneDrive(withRoot);
        Graph.Clock.SetUtcNow(Now);
        Graph.Stub.UseDrive(Drive);
        Environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["HOME"] = Path.Combine(Tree.Root, "home"),
            ["ZYGGY_STATE_DIR"] = Path.Combine(Tree.Root, "state"),
        };
        StatePaths = new ArchiveStatePaths(Environment);
        ActionLogDirectory = Path.Combine(Tree.Root, "state", "m365");
    }

    public MemoryTree Tree { get; } = new();

    public RecordingProcessRunner Git { get; } = new();

    public GraphFixture Graph { get; } = new();

    public StubOneDrive Drive { get; }

    public Dictionary<string, string?> Environment { get; }

    public ArchiveStatePaths StatePaths { get; }

    public string ActionLogDirectory { get; set; }

    public OffloadState State { get; set; } = new();

    public OffloadOptions Options { get; set; } = new() { Enabled = true };

    /// <summary>The scope limit the writer is built with (the simple-upload limit unless a test lowers it).</summary>
    public long SimpleUploadLimit { get; set; } = DriveWriteScope.SimpleUploadLimit;

    public IReadOnlyList<string> ActionRows =>
        File.Exists(Path.Combine(ActionLogDirectory, "actions.jsonl")) ? File.ReadAllLines(Path.Combine(ActionLogDirectory, "actions.jsonl")) : [];

    public static TimeZoneInfo Zone { get; } = TimeZoneInfo.CreateCustomTimeZone("Europe/Brussels", TimeSpan.FromHours(2), "Europe/Brussels", "Europe/Brussels");

    public static string Golden(string name) => Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(Infrastructure.Golden.Directory, "archive", name)));

    /// <summary>The 68-byte text item with 37's golden sidecar, indexed by a line in the project file.</summary>
    public void QuoteItem() => Item("quote-2026", Encoding.UTF8.GetBytes(TextItem), Golden("sidecar-text.md"));

    /// <summary>An indexed item with a sidecar matching its bytes (37's text sidecar values, this item's size and hash).</summary>
    public void Item(string slug, byte[] bytes, string? sidecar = null, string extension = "txt", string project = "zyggy", bool indexed = true)
    {
        var path = $"archive/{project}/{slug}.{extension}";
        var full = Tree.Full(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
        var type = extension switch { "png" => ArchiveMediaType.Png, "pdf" => ArchiveMediaType.Pdf, _ => ArchiveMediaType.TextPlain };
        sidecar ??= MemoryFileWriter.Render(new ArchiveSidecar(
            "Quote 2026", "Roof repair quote", Slug.Parse(project), type, bytes.LongLength, Convert.ToHexStringLower(SHA256.HashData(bytes)),
            new DateOnly(2026, 9, 30), "quote.txt", new DateOnly(2026, 9, 30)).ToMemoryFile());
        Tree.Write($"archive/{project}/{slug}.md", sidecar);
        if (indexed)
        {
            _projectLines.Add($"- [stated] 2026-09-30: Archived \"Quote 2026\" at {path} — Roof repair quote");
            Tree.Write($"business/areas/{project}.md", $"---\nname: {project}\ndescription: {project}\nupdated: 2026-10-01\n---\n" + string.Concat(_projectLines.Select(l => l + "\n")));
        }
    }

    public OffloadService Service()
    {
        var secrets = SecretPatterns.Load(Path.Combine(Infrastructure.Golden.Directory, "secret-patterns", "secret-patterns.txt")).Patterns!;
        var options = new ArchiveOptions { Offload = Options };
        var git = new GitClient(Git, new GitClientOptions(), Graph.Clock);
        var publisher = new MemoryPublisher(git);
        var archive = new ArchiveService(Tree.Paths, Zone, Graph.Clock, secrets, options, SourceDenyList.Build(Environment, options, Tree.Paths), publisher, Tree.Root);
        return new OffloadService(
            Tree.Paths, Zone, Graph.Clock, secrets, Options, State, archive, publisher, git, Tree.Root, Graph.Reader,
            scope => new GraphDriveWriter(Graph.Tokens, Graph.Http, scope),
            new ActionLog(ActionLogDirectory))
        {
            StatePaths = StatePaths,
            SimpleUploadLimit = SimpleUploadLimit,
        };
    }

    public Task<OffloadRun> RunAsync() => Service().RunAsync(TestContext.Current.CancellationToken);

    public void Dispose()
    {
        Graph.Dispose();
        Tree.Dispose();
    }
}
