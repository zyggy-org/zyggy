using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Git;
using Zyggy.Core.Memory;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Memory;

/// <summary>
/// A passed <c>archive add</c> (spec 37 AC-13..AC-15): the item bytes, the sidecar golden, the index line byte-identical to
/// <c>memory remember</c>, one <c>commit --only</c> of the two archive paths with the golden message, the push rule, and every git
/// failure as a <c>GitError</c> outcome (fake process runner, fake clock).
/// </summary>
public sealed class ArchiveServiceTests : IDisposable
{
    private const string TextItem = "Quote for the Zyggy roof repair.\nTotal 1 234,00 EUR, valid 30 days.\n";

    private const string Rejected = " ! [rejected]        HEAD -> main (fetch first)\nerror: failed to push some refs";

    private const string IndexLine =
        "- [stated] 2026-09-30 (project:zyggy): Archived \"Quote 2026\" (text/plain, 68 B) at archive/zyggy/quote-2026.txt — Roof repair quote";

    private const string Locked = "fatal: Unable to create '/srv/memory/.git/index.lock': File exists.";

    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("Europe/Brussels", TimeSpan.FromHours(2), "Test/Brussels", "Test/Brussels");

    private static readonly string[] CommitPaths = ["acme/alice/archive/zyggy/quote-2026.md", "acme/alice/archive/zyggy/quote-2026.txt"];

    private readonly MemoryTree _tree = new();
    private readonly RecordingProcessRunner _git = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero));
    private readonly string _home;
    private readonly string _source;
    private SecretPatterns? _indexLinePatterns;

    public ArchiveServiceTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "zyggy-ut-service", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_home, "Downloads"));
        _source = Path.Combine(_home, "Downloads", "quote.txt");
        File.WriteAllBytes(_source, new UTF8Encoding(false).GetBytes(TextItem));
    }

    private string Item => _tree.Full("archive/zyggy/quote-2026.txt");

    private string Sidecar => _tree.Full("archive/zyggy/quote-2026.md");

    private string Inbox => _tree.Full("inbox/remember-2026-09-30.md");

    public void Dispose()
    {
        _tree.Dispose();
        if (Directory.Exists(_home))
        {
            Directory.Delete(_home, recursive: true);
        }
    }

    [Fact]
    public async Task Add_Valid_ItemBytesEqualSourceAndSha256Matches()
    {
        // Act
        var outcome = await AddAsync();

        // Assert
        outcome.Kind.Should().Be(ArchiveOutcomeKind.Archived);
        var bytes = await File.ReadAllBytesAsync(Item, TestContext.Current.CancellationToken);
        bytes.Should().Equal(new UTF8Encoding(false).GetBytes(TextItem));
        ArchiveSidecar.TryParse(MemoryFileReader.Read(Sidecar))!.Sha256.Should().Be(Convert.ToHexStringLower(SHA256.HashData(bytes)));
        outcome.Item.Should().Be("archive/zyggy/quote-2026.txt");
        outcome.Sidecar.Should().Be("archive/zyggy/quote-2026.md");
    }

    [Fact]
    public async Task Add_Valid_SidecarEqualsGolden()
    {
        // Act
        await AddAsync();

        // Assert
        (await File.ReadAllBytesAsync(Sidecar, TestContext.Current.CancellationToken))
            .Should().Equal(await File.ReadAllBytesAsync(Path.Combine(Golden.Directory, "archive", "sidecar-text.md"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Add_Valid_InboxLineByteIdenticalToRememberService()
    {
        // Arrange
        using var other = new MemoryTree();
        var fact = IndexLine[(IndexLine.IndexOf(": ", StringComparison.Ordinal) + 2)..];
        new RememberService(other.Paths, Zone, _clock, Patterns()).Remember(new RememberRequest("stated", " (project:zyggy)", "", fact));

        // Act
        var outcome = await AddAsync();

        // Assert
        var inbox = await File.ReadAllBytesAsync(Inbox, TestContext.Current.CancellationToken);
        inbox.Should().Equal(await File.ReadAllBytesAsync(other.Full("inbox/remember-2026-09-30.md"), TestContext.Current.CancellationToken));
        var golden = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(Path.Combine(Golden.Directory, "archive", "inbox-after-archive.md"), TestContext.Current.CancellationToken));
        Encoding.UTF8.GetString(inbox).Should().Be(golden[..golden.LastIndexOf("- [stated]", StringComparison.Ordinal)]);
        outcome.Line.Should().Be(IndexLine);
        outcome.InboxPath.Should().Be(Inbox);
    }

    [Fact]
    public async Task Add_Valid_CommitMessageEqualsGoldenAndPathsAreExactlyTwo()
    {
        // Act
        var outcome = await AddAsync();

        // Assert
        var commit = _git.CallsOf("commit").Single();
        commit.StandardInput.Should().Be(await File.ReadAllTextAsync(Path.Combine(Golden.Directory, "archive", "commit-message-add.txt"), TestContext.Current.CancellationToken));
        commit.Arguments.Should().Equal("commit", "--only", "-F", "-", "--", CommitPaths[0], CommitPaths[1]);
        _git.CallsOf("add").Single().Arguments.Should().Equal("add", "--", CommitPaths[0], CommitPaths[1]);
        _git.Calls.SelectMany(c => c.Arguments).Should().NotContain(a => a.Contains("inbox/", StringComparison.Ordinal));
        outcome.Sha.Should().Be("0123456789abcdef0123456789abcdef01234567");
        outcome.Pushed.Should().BeTrue();
    }

    [Fact]
    public async Task Add_Valid_WriteOrderIsItemSidecarInboxThenGit()
    {
        // Arrange
        var atRevList = new List<bool>();
        var atAdd = new List<bool>();
        _git.Hook = spec =>
        {
            var probes = RecordingProcessRunner.SubCommand(spec) switch
            {
                "rev-list" => atRevList,
                "add" => atAdd,
                _ => null,
            };
            probes?.AddRange([File.Exists(Item), File.Exists(Sidecar), File.Exists(Inbox)]);
            return null;
        };

        // Act
        await AddAsync();

        // Assert
        atRevList.Should().Equal(false, false, false);
        atAdd.Should().Equal(true, true, true);
        _git.Calls.Select(RecordingProcessRunner.SubCommand).Should().Equal("symbolic-ref", "rev-parse", "rev-list", "add", "commit", "rev-parse", "push");
    }

    [Fact]
    public async Task Add_Valid_NoTempFileLeft()
    {
        // Act
        await AddAsync();

        // Assert
        Directory.EnumerateFiles(_tree.Root, "*.zyggy-tmp-*", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task Add_PreflightNotOnBranch_GitErrorNothingWritten()
    {
        // Arrange
        _git.On("symbolic-ref", RecordingProcessRunner.Fail(1, ""));
        var before = Disk();

        // Act
        var outcome = await AddAsync();

        // Assert
        outcome.Kind.Should().Be(ArchiveOutcomeKind.GitError);
        outcome.Detail.Should().Be("not_on_branch");
        Disk().Should().Equal(before);
    }

    [Fact]
    public async Task Add_PreflightOperationInProgress_GitErrorNothingWritten()
    {
        // Arrange
        var rebase = Path.Combine(_tree.Root, ".git", "rebase-merge");
        Directory.CreateDirectory(rebase);
        _git.Hook = spec => spec.Arguments.Contains("--git-path") ? RecordingProcessRunner.Ok(".git/rebase-merge\n.git/rebase-apply\n.git/MERGE_HEAD\n") : null;
        var before = Disk();

        // Act
        var outcome = await AddAsync();

        // Assert
        outcome.Kind.Should().Be(ArchiveOutcomeKind.GitError);
        outcome.Detail.Should().Be("operation_in_progress");
        Disk().Should().Equal(before);
    }

    [Fact]
    public async Task Add_CommitFails_GitErrorCommitFailedFilesLeftOnDiskInboxLineWritten()
    {
        // Arrange
        _git.On("commit", RecordingProcessRunner.Fail(1, "fatal: empty ident name"));

        // Act
        var outcome = await AddAsync();

        // Assert
        outcome.Kind.Should().Be(ArchiveOutcomeKind.GitError);
        outcome.Detail.Should().Be("commit_failed");
        File.Exists(Item).Should().BeTrue();
        File.Exists(Sidecar).Should().BeTrue();
        (await File.ReadAllTextAsync(Inbox, TestContext.Current.CancellationToken)).Should().EndWith(IndexLine + "\n");
        _git.CallsOf("push").Should().BeEmpty();
    }

    [Fact]
    public async Task Add_PushRejectedAfterRetry_ArchivedPushedFalse()
    {
        // Arrange
        _git.On("push", RecordingProcessRunner.Fail(1, Rejected), RecordingProcessRunner.Fail(1, Rejected));

        // Act
        var outcome = await AddAsync();

        // Assert
        outcome.Kind.Should().Be(ArchiveOutcomeKind.Archived);
        outcome.Pushed.Should().BeFalse();
        outcome.Sha.Should().NotBeNull();
        _git.CallsOf("fetch").Should().ContainSingle();
        _git.CallsOf("rebase").Should().ContainSingle();
        _git.CallsOf("push").Should().HaveCount(2);
    }

    [Fact]
    public async Task Add_IndexLock_RetriedThreeTimesTwoSecondBackoff()
    {
        // Arrange
        var times = new List<DateTimeOffset>();
        _git.Hook = spec =>
        {
            if (RecordingProcessRunner.SubCommand(spec) == "commit")
            {
                times.Add(_clock.GetUtcNow());
            }

            return null;
        };
        var locked = RecordingProcessRunner.Fail(128, Locked);
        _git.On("commit", locked, locked, locked, RecordingProcessRunner.Ok());

        // Act
        var outcome = await RunAdvancingClockAsync();

        // Assert
        outcome.Kind.Should().Be(ArchiveOutcomeKind.Archived);
        times.Should().HaveCount(4);
        times.Zip(times.Skip(1), (a, b) => b - a).Should().AllSatisfy(gap => gap.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Add_IndexLockPersists_GitErrorIndexLock()
    {
        // Arrange
        var locked = RecordingProcessRunner.Fail(128, Locked);
        _git.On("commit", locked, locked, locked, locked, RecordingProcessRunner.Ok());

        // Act
        var outcome = await RunAdvancingClockAsync();

        // Assert
        outcome.Kind.Should().Be(ArchiveOutcomeKind.GitError);
        outcome.Detail.Should().Be("index_lock");
        _git.CallsOf("commit").Should().HaveCount(4);
        _git.CallsOf("push").Should().BeEmpty();
    }

    [Fact]
    public async Task Add_ProjectFileMissing_NoteSet()
    {
        // Act
        var outcome = await AddAsync();

        // Assert
        outcome.Note.Should().Be("no memory file named zyggy yet; the dream will create it");
    }

    [Fact]
    public async Task Add_ProjectFileExists_NoNote()
    {
        // Arrange
        _tree.Write("business/areas/zyggy.md", "---\nname: Zyggy\ndescription: the agent platform\nupdated: 2026-09-01\n---\n");

        // Act
        var outcome = await AddAsync();

        // Assert
        outcome.Note.Should().BeNull();
    }

    [Fact]
    public async Task Add_RememberRefusesUnexpectedly_ReportsRefusalFilesLeft()
    {
        // Arrange: a pattern set that matches the composed line, injected after the check phase.
        var patterns = Path.Combine(_home, "late-patterns.txt");
        await File.WriteAllTextAsync(patterns, "archived-word\tArchived\n", TestContext.Current.CancellationToken);
        _indexLinePatterns = SecretPatterns.Load(patterns).Patterns!;

        // Act
        var outcome = await AddAsync();

        // Assert
        outcome.Kind.Should().Be(ArchiveOutcomeKind.Refused);
        outcome.Refusal.Should().Be(ArchiveRefusal.SecretPattern);
        outcome.Detail.Should().Be("archived-word (line)");
        File.Exists(Item).Should().BeTrue();
        File.Exists(Sidecar).Should().BeTrue();
        File.Exists(Inbox).Should().BeFalse();
        _git.CallsOf("commit").Should().BeEmpty();
    }

    private static SecretPatterns Patterns() => SecretPatterns.Load(Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt")).Patterns!;

    private ArchiveAddRequest Request() => new(Slug.Parse("zyggy"), "Quote 2026", "Roof repair quote", _source, Slug.Parse("quote-2026"));

    private ArchiveService Service()
    {
        var options = new ArchiveOptions();
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal) { ["HOME"] = _home };
        var publisher = new MemoryPublisher(new GitClient(_git, new GitClientOptions(), _clock));
        return new ArchiveService(_tree.Paths, Zone, _clock, Patterns(), options, SourceDenyList.Build(environment, options, _tree.Paths), publisher, _tree.Root)
        {
            IndexLinePatterns = _indexLinePatterns ?? Patterns(),
        };
    }

    private Task<ArchiveOutcome> AddAsync() => Service().AddAsync(Request(), TestContext.Current.CancellationToken);

    private async Task<ArchiveOutcome> RunAdvancingClockAsync()
    {
        var task = AddAsync();
        while (!task.IsCompleted)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }

        return await task;
    }

    private Dictionary<string, string> Disk() =>
        Directory.EnumerateFiles(_tree.Root, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, f => Convert.ToHexString(File.ReadAllBytes(f)), StringComparer.Ordinal);
}
