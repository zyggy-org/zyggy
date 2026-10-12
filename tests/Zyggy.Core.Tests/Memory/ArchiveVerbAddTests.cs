using System.Text;

using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Memory;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Memory;

/// <summary>A passed <c>zyggy memory archive add</c> (spec 37 AC-17): the four stdout lines, exit 0; exit 7 on a deferred push; exit 6 on a git error.</summary>
public sealed class ArchiveVerbAddTests : IDisposable
{
    private const string Rejected = " ! [rejected]        HEAD -> main (fetch first)\nerror: failed to push some refs";

    private const string IndexLine =
        "- [stated] 2026-09-30 (project:zyggy): Archived \"Quote 2026\" (text/plain, 68 B) at archive/zyggy/quote-2026.txt — Roof repair quote";

    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private readonly MemoryTree _tree = new();
    private readonly RecordingProcessRunner _git = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero));
    private readonly string _home;
    private readonly string[] _args;
    private readonly Dictionary<string, string?> _environment;

    public ArchiveVerbAddTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "zyggy-ut-verb-add", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_home, "Downloads"));
        var source = Path.Combine(_home, "Downloads", "quote.txt");
        File.WriteAllBytes(source, new UTF8Encoding(false).GetBytes("Quote for the Zyggy roof repair.\nTotal 1 234,00 EUR, valid 30 days.\n"));
        _args = ["add", "--project", "zyggy", "--name", "Quote 2026", "--description", "Roof repair quote", "--file", source];
        _environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["HOME"] = _home,
            ["ZYGGY_MEMORY_ROOT"] = _tree.Root,
            ["ZYGGY_TENANT"] = "acme",
            ["ZYGGY_USER"] = "alice",
            ["ZYGGY_TIMEZONE"] = "Europe/Brussels",
            ["ZYGGY_SECRET_PATTERNS"] = Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt"),
        };
        _tree.Write("business/areas/zyggy.md", "---\nname: Zyggy\ndescription: the agent platform\nupdated: 2026-09-01\n---\n");
    }

    public void Dispose()
    {
        _tree.Dispose();
        if (Directory.Exists(_home))
        {
            Directory.Delete(_home, recursive: true);
        }
    }

    [Fact]
    public async Task Run_Valid_StdoutFourLinesExitZero()
    {
        // Act
        var (exit, console) = await RunAsync();

        // Assert
        exit.Should().Be(0);
        console.Stdout.Should().Be($"archived: archive/zyggy/quote-2026.txt\nsidecar: archive/zyggy/quote-2026.md\n{IndexLine}\ncommit: {Sha} pushed\n");
        console.Stderr.Should().BeEmpty();
        File.Exists(_tree.Full("archive/zyggy/quote-2026.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task Run_PushDeferred_ExitSevenStderrCommittedShaPushDeferred()
    {
        // Arrange
        _git.On("push", RecordingProcessRunner.Fail(1, Rejected), RecordingProcessRunner.Fail(1, Rejected));

        // Act
        var (exit, console) = await RunAsync();

        // Assert
        exit.Should().Be(7);
        console.Stdout.Should().EndWith($"commit: {Sha} push deferred\n");
        console.Stderr.Should().Be($"archive: committed {Sha}, push deferred\n");
    }

    [Fact]
    public async Task Run_GitError_ExitSixStderrNamesStep()
    {
        // Arrange
        _git.On("symbolic-ref", RecordingProcessRunner.Fail(1, ""));

        // Act
        var (exit, console) = await RunAsync();

        // Assert
        exit.Should().Be(6);
        console.Stdout.Should().BeEmpty();
        console.Stderr.Should().Be("archive: git error: not_on_branch\n");
        Directory.Exists(_tree.Full("archive")).Should().BeFalse();
    }

    [Fact]
    public async Task Run_Valid_StderrHasOnlyTheNoteWhenProjectFileMissing()
    {
        // Arrange
        File.Delete(_tree.Full("business/areas/zyggy.md"));

        // Act
        var (exit, console) = await RunAsync();

        // Assert
        exit.Should().Be(0);
        console.Stderr.Should().Be("archive: note: no memory file named zyggy yet; the dream will create it\n");
    }

    [Fact]
    public async Task Run_Docx_StdoutFourLinesLabelLine()
    {
        // Arrange
        var source = Path.Combine(_home, "Downloads", "sample.docx");
        File.Copy(Path.Combine(Golden.Directory, "office", "sample.docx"), source);
        string[] args = ["add", "--project", "zyggy", "--name", "Roof offer", "--description", "Contractor offer for the roof repair", "--file", source];

        // Act
        var (exit, console) = await RunAsync(args);

        // Assert
        exit.Should().Be(0, console.Stderr);
        console.Stdout.Should().Be(
            "archived: archive/zyggy/roof-offer.docx\nsidecar: archive/zyggy/roof-offer.md\n"
            + "- [stated] 2026-09-30 (project:zyggy): Archived \"Roof offer\" (Word document, 1.3 KB) at archive/zyggy/roof-offer.docx — Contractor offer for the roof repair\n"
            + $"commit: {Sha} pushed\n");
        console.Stderr.Should().BeEmpty();
    }

    [Fact]
    public async Task Run_PptxLongestExactly400_ArchivedWithLine399AndPreCheckRefuses401()
    {
        // Arrange: plan assumption A2 at the verb — Longest is exactly 400 for this request; the committed deck's line is 399 and is
        // archived, while one more character fails the usage-time pre-check with the message the composed-line check reuses. A deck whose
        // line would reach 401 needs an eight-character size (100 MB or more), which too_large refuses first under the 25 MiB ceiling.
        var source = Path.Combine(_home, "Downloads", "deck.pptx");
        File.Copy(Path.Combine(Golden.Directory, "office", "sample.pptx"), source);
        var name = Words(100);
        var slug = "roof-pitch-" + new string('a', 49);
        var bare = new ArchiveAddRequest(Slug.Parse("zyggy"), name, string.Empty, source, Slug.Parse(slug));
        var description = Words(400 - ArchiveIndexLine.Longest(new DateOnly(2026, 9, 30), bare).Length);
        var preCheck = Words(description.Length + 1);
        var (preExit, preConsole) = await RunAsync(["add", "--project", "zyggy", "--name", name, "--description", preCheck, "--slug", slug, "--file", source]);

        // Act
        var (exit, console) = await RunAsync(["add", "--project", "zyggy", "--name", name, "--description", description, "--slug", slug, "--file", source]);

        // Assert
        preExit.Should().Be(4);
        preConsole.Stderr.Should().Be("archive: the index line would exceed 400 characters (401): shorten --name or --description\n");
        exit.Should().Be(0, console.Stderr);
        console.Stdout.Split('\n')[2].Should().HaveLength(399).And.Contain("(PowerPoint deck, 1.3 KB)");
    }

    // A description of the given length that no secret pattern matches: runs of nine letters separated by spaces, never ending in one.
    private static string Words(int length) =>
        string.Create(length, 0, (span, _) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = i % 10 == 9 && i != span.Length - 1 ? ' ' : 'x';
            }
        });

    private Task<(int Exit, VerbConsole Console)> RunAsync() => RunAsync(_args);

    private async Task<(int Exit, VerbConsole Console)> RunAsync(string[] args)
    {
        var console = new VerbConsole();
        var exit = await new ArchiveVerb(_environment, _clock, FindTimeZone, _git).RunAsync(args, console.Io, TestContext.Current.CancellationToken);
        return (exit, console);
    }

    private static TimeZoneInfo FindTimeZone(string id) =>
        id == "Europe/Brussels"
            ? TimeZoneInfo.CreateCustomTimeZone("Europe/Brussels", TimeSpan.FromHours(2), "Test/Brussels", "Test/Brussels")
            : TimeZoneInfo.FindSystemTimeZoneById(id);
}
