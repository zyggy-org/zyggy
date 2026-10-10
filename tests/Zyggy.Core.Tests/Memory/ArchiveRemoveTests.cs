using System.Text;

using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Memory;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Memory;

/// <summary>
/// <c>zyggy memory archive remove &lt;project&gt;/&lt;slug&gt;</c> (spec 37 AC-19): <c>git rm</c> of the paths present, one <c>archive remove</c>
/// commit pushed with the one-rebase rule, one <c>Removed archived item …</c> inbox line; <c>not_found</c> when neither file exists; the
/// project's file is never edited (fake process runner, fake clock).
/// </summary>
public sealed class ArchiveRemoveTests : IDisposable
{
    private const string Rejected = " ! [rejected]        HEAD -> main (fetch first)\nerror: failed to push some refs";

    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private const string ProjectFile =
        "---\nname: zyggy\ndescription: Zyggy\nupdated: 2026-10-01\n---\n- [stated] 2026-09-30: Archived \"Quote 2026\" (text/plain, 68 B) at archive/zyggy/quote-2026.txt — Roof repair quote\n";

    private static readonly string[] RepoPaths = ["acme/alice/archive/zyggy/quote-2026.md", "acme/alice/archive/zyggy/quote-2026.txt"];

    private readonly MemoryTree _tree = new();
    private readonly RecordingProcessRunner _git = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero));
    private readonly Dictionary<string, string?> _environment;

    public ArchiveRemoveTests()
    {
        _environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["HOME"] = Path.Combine(_tree.Root, "home"),
            ["ZYGGY_MEMORY_ROOT"] = _tree.Root,
            ["ZYGGY_TENANT"] = "acme",
            ["ZYGGY_USER"] = "alice",
            ["ZYGGY_TIMEZONE"] = "Europe/Brussels",
            ["ZYGGY_SECRET_PATTERNS"] = Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt"),
        };
        _tree.Write("business/areas/zyggy.md", ProjectFile);
    }

    public void Dispose() => _tree.Dispose();

    private static TimeZoneInfo FindTimeZone(string id) => TimeZoneInfo.CreateCustomTimeZone(id, TimeSpan.FromHours(2), id, id);

    private static string GoldenText(string name) =>
        Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(Golden.Directory, "archive", name)));

    private void BothFiles()
    {
        _tree.Write("archive/zyggy/quote-2026.txt", "Quote for the Zyggy roof repair.\nTotal 1 234,00 EUR, valid 30 days.\n");
        _tree.Write("archive/zyggy/quote-2026.md", GoldenText("sidecar-text.md"));
    }

    private async Task<(int Exit, VerbConsole Console)> RunAsync(string reference = "zyggy/quote-2026")
    {
        var console = new VerbConsole();
        var exit = await new ArchiveVerb(_environment, _clock, FindTimeZone, _git).RunAsync(["remove", reference], console.Io, TestContext.Current.CancellationToken);
        return (exit, console);
    }

    [Fact]
    public async Task Remove_Existing_GitRmBothPathsCommitOnlyBothPushed()
    {
        // Arrange
        BothFiles();

        // Act
        var (exit, console) = await RunAsync();

        // Assert
        exit.Should().Be(ArchiveVerb.Archived, console.Stderr);
        _git.CallsOf("rm").Should().ContainSingle().Which.Arguments.Should().Equal("rm", "--", RepoPaths[0], RepoPaths[1]);
        var commit = _git.CallsOf("commit").Should().ContainSingle().Subject;
        commit.Arguments.Should().Equal("commit", "--only", "-F", "-", "--", RepoPaths[0], RepoPaths[1]);
        commit.StandardInput.Should().Be(GoldenText("commit-message-remove.txt"));
        _git.CallsOf("push").Should().ContainSingle().Which.Arguments.Should().Equal("push", "origin", "HEAD:main");
        _git.CallsOf("add").Should().BeEmpty();
        var lines = GoldenText("inbox-after-archive.md").TrimEnd('\n').Split('\n');
        console.Stdout.Should().Be(
            $"removed: archive/zyggy/quote-2026.txt\nremoved: archive/zyggy/quote-2026.md\n{lines[^1]}\ncommit: {Sha} pushed\n");
    }

    [Fact]
    public async Task Remove_Existing_InboxLineEqualsGoldenSecondLine()
    {
        // Arrange
        BothFiles();

        // Act
        await RunAsync();

        // Assert
        var inbox = File.ReadAllText(_tree.Full("inbox/remember-2026-09-30.md"));
        var golden = GoldenText("inbox-after-archive.md");
        var removeLine = golden.TrimEnd('\n').Split('\n')[^1];
        inbox.Should().EndWith("---\n" + removeLine + "\n");
        removeLine.Should().Be("- [stated] 2026-09-30 (project:zyggy): Removed archived item archive/zyggy/quote-2026.txt (\"Quote 2026\")");
    }

    [Fact]
    public async Task Remove_Existing_ProjectFileLineUntouched()
    {
        // Arrange
        BothFiles();

        // Act
        await RunAsync();

        // Assert
        File.ReadAllText(_tree.Full("business/areas/zyggy.md")).Should().Be(ProjectFile);
        _git.Calls.SelectMany(c => c.Arguments).Should().NotContain(a => a.Contains("business/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Remove_Missing_RefusedNotFoundNothingRun()
    {
        // Act
        var (exit, console) = await RunAsync("zyggy/nothing-here");

        // Assert
        exit.Should().Be(ArchiveVerb.Refused);
        console.Stdout.Should().BeEmpty();
        console.Stderr.Should().Be("archive: refused: not_found\n");
        _git.Calls.Select(RecordingProcessRunner.SubCommand).Should().NotContain(["rm", "commit", "push", "add"]);
        Directory.Exists(_tree.Full("inbox")).Should().BeFalse();
    }

    [Fact]
    public async Task Remove_SidecarOnly_RemovesSidecarNameFromSidecar()
    {
        // Arrange: the item file is gone (orphan sidecar); the sidecar still names its type and the name.
        _tree.Write("archive/zyggy/quote-2026.md", GoldenText("sidecar-text.md"));

        // Act
        var (exit, console) = await RunAsync();

        // Assert
        exit.Should().Be(ArchiveVerb.Archived, console.Stderr);
        _git.CallsOf("rm").Single().Arguments.Should().Equal("rm", "--", RepoPaths[0]);
        _git.CallsOf("commit").Single().Arguments.Should().Equal("commit", "--only", "-F", "-", "--", RepoPaths[0]);
        console.Stdout.Should().StartWith(
            "removed: archive/zyggy/quote-2026.md\n- [stated] 2026-09-30 (project:zyggy): Removed archived item archive/zyggy/quote-2026.txt (\"Quote 2026\")\n");
    }

    [Fact]
    public async Task Remove_ItemOnly_RemovesItemNameDash()
    {
        // Arrange: the sidecar is gone (plan assumption A4: the name is "-").
        _tree.Write("archive/zyggy/quote-2026.txt", "Quote for the Zyggy roof repair.\n");

        // Act
        var (exit, console) = await RunAsync();

        // Assert
        exit.Should().Be(ArchiveVerb.Archived, console.Stderr);
        _git.CallsOf("rm").Single().Arguments.Should().Equal("rm", "--", RepoPaths[1]);
        _git.CallsOf("commit").Single().StandardInput.Should().Be(GoldenText("commit-message-remove.txt"));
        console.Stdout.Should().StartWith(
            "removed: archive/zyggy/quote-2026.txt\n- [stated] 2026-09-30 (project:zyggy): Removed archived item archive/zyggy/quote-2026.txt (\"-\")\n");
    }

    [Fact]
    public async Task Remove_PushRejectedConflict_PushedFalse()
    {
        // Arrange
        BothFiles();
        _git.On("push", RecordingProcessRunner.Fail(1, Rejected));
        _git.Hook = spec => RecordingProcessRunner.SubCommand(spec) == "rebase" && !spec.Arguments.Contains("--abort")
            ? RecordingProcessRunner.Fail(1, "CONFLICT (modify/delete): acme/alice/archive/zyggy/quote-2026.md")
            : null;

        // Act
        var (exit, console) = await RunAsync();

        // Assert
        exit.Should().Be(ArchiveVerb.PushDeferred);
        console.Stdout.Should().EndWith($"commit: {Sha} push deferred\n");
        console.Stderr.Should().Be($"archive: committed {Sha}, push deferred\n");
    }

    [Fact]
    public async Task Remove_PreflightNotOnBranch_GitErrorNothingRemoved()
    {
        // Arrange
        BothFiles();
        _git.On("symbolic-ref", RecordingProcessRunner.Fail(1, ""));

        // Act
        var (exit, console) = await RunAsync();

        // Assert
        exit.Should().Be(ArchiveVerb.GitError);
        console.Stderr.Should().Be("archive: git error: not_on_branch\n");
        _git.CallsOf("rm").Should().BeEmpty();
        File.Exists(_tree.Full("archive/zyggy/quote-2026.txt")).Should().BeTrue();
        Directory.Exists(_tree.Full("inbox")).Should().BeFalse();
    }

    [Fact]
    public async Task Remove_UnpushedArchiveCommit_PushesFirst()
    {
        // Arrange
        BothFiles();
        _git.On("rev-list", RecordingProcessRunner.Ok("commit 1111\narchive add zyggy/other.txt\n"));

        // Act
        var (exit, console) = await RunAsync();

        // Assert
        exit.Should().Be(ArchiveVerb.Archived, console.Stderr);
        _git.Calls.Select(RecordingProcessRunner.SubCommand).Where(s => s is "push" or "rm").Should().Equal("push", "rm", "push");
    }
}
