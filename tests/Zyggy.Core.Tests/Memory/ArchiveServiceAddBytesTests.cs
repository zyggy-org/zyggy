using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Git;
using Zyggy.Core.Memory;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Memory;

/// <summary>
/// Spec 41 AC-30 (unit half), AC-33 (check half), AC-38 (git half), AC-39, AC-40 (writer half): <see cref="ArchiveService.AddBytesAsync"/>
/// files given bytes through the one archive writer exactly as <c>archive add</c> does, and recognises an item already filed by its
/// sidecar's SHA-256 (fake process runner, fake clock, temp memory tree).
/// </summary>
public sealed class ArchiveServiceAddBytesTests : IDisposable
{
    private const string TextItem = "Quote for the Zyggy roof repair.\nTotal 1 234,00 EUR, valid 30 days.\n";

    private const string Rejected = " ! [rejected]        HEAD -> main (fetch first)\nerror: failed to push some refs";

    private const string Locked = "fatal: Unable to create '/srv/memory/.git/index.lock': File exists.";

    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("Europe/Brussels", TimeSpan.FromHours(2), "Test/Brussels", "Test/Brussels");

    private readonly MemoryTree _tree = new();
    private readonly RecordingProcessRunner _git = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero));
    private readonly string _home;

    public ArchiveServiceAddBytesTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "zyggy-ut-addbytes", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_home, "Downloads"));
    }

    private static byte[] TextBytes => new UTF8Encoding(false).GetBytes(TextItem);

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
    public async Task AddBytesAsync_SameInputsAsAddAsync_ItemSidecarIndexLineAndCommitMessageByteIdentical()
    {
        // Arrange: the same item archived from a file through AddAsync in a second tree with its own recorded git.
        using var other = new MemoryTree();
        var otherGit = new RecordingProcessRunner();
        var source = Path.Combine(_home, "Downloads", "quote.txt");
        await File.WriteAllBytesAsync(source, TextBytes, TestContext.Current.CancellationToken);
        var viaAdd = await Service(other, otherGit).AddAsync(
            new ArchiveAddRequest(Slug.Parse("zyggy"), "Quote 2026", "Roof repair quote", source, Slug.Parse("quote-2026")),
            TestContext.Current.CancellationToken);

        // Act
        var outcome = await Service().AddBytesAsync(Bytes(), TestContext.Current.CancellationToken);

        // Assert
        viaAdd.Kind.Should().Be(ArchiveOutcomeKind.Archived);
        outcome.Kind.Should().Be(ArchiveOutcomeKind.Archived);
        outcome.Line.Should().Be(viaAdd.Line);
        (await File.ReadAllBytesAsync(Item, TestContext.Current.CancellationToken)).Should().Equal(TextBytes);
        (await File.ReadAllBytesAsync(Sidecar, TestContext.Current.CancellationToken))
            .Should().Equal(await File.ReadAllBytesAsync(Path.Combine(Golden.Directory, "archive", "sidecar-text.md"), TestContext.Current.CancellationToken));
        (await File.ReadAllBytesAsync(Inbox, TestContext.Current.CancellationToken))
            .Should().Equal(await File.ReadAllBytesAsync(other.Full("inbox/remember-2026-09-30.md"), TestContext.Current.CancellationToken));
        _git.CallsOf("commit").Single().StandardInput.Should().Be(otherGit.CallsOf("commit").Single().StandardInput);
        _git.CallsOf("commit").Single().Arguments.Should().Equal(otherGit.CallsOf("commit").Single().Arguments);
    }

    [Fact]
    public async Task AddBytesAsync_MarkdownSourceName_StoredTxtTypeTextMarkdown()
    {
        // Arrange
        var request = Bytes() with { SourceName = "restore-notes.md", Bytes = "# Restore\n\nSteps for the staging restore.\n"u8.ToArray() };

        // Act
        var outcome = await Service().AddBytesAsync(request, TestContext.Current.CancellationToken);

        // Assert
        outcome.Kind.Should().Be(ArchiveOutcomeKind.Archived);
        outcome.Item.Should().Be("archive/zyggy/quote-2026.txt");
        var sidecar = ArchiveSidecar.TryParse(MemoryFileReader.Read(Sidecar))!;
        sidecar.MediaType.Should().Be(ArchiveMediaType.TextMarkdown);
        sidecar.SourceName.Should().Be("restore-notes.md");
    }

    [Theory]
    [InlineData("type_not_allowed")]
    [InlineData("project_cap")]
    [InlineData("total_cap")]
    [InlineData("slug_taken_other_sha")]
    [InlineData("secret_line")]
    [InlineData("contact_detail")]
    [InlineData("office_refused")]
    public async Task AddBytesAsync_TypeNotAllowed_ProjectCap_TotalCap_SlugTakenOtherSha_SecretLine_ContactDetail_OfficeRefused_Refused_NothingWritten(string scenario)
    {
        // Arrange
        var options = new ArchiveOptions();
        var request = Bytes();
        ArchiveRefusal expected;
        switch (scenario)
        {
            case "type_not_allowed":
                options = options with { AllowedTypes = new HashSet<ArchiveMediaType> { ArchiveMediaType.Pdf } };
                expected = ArchiveRefusal.TypeNotAllowed;
                break;
            case "project_cap":
                _tree.Write("archive/zyggy/old.txt", "Old roof notes from last year.\n");
                options = options with { ItemMaxBytes = 80, ProjectMaxBytes = 90, TotalMaxBytes = 1000 };
                expected = ArchiveRefusal.ProjectCap;
                break;
            case "total_cap":
                _tree.Write("archive/other/old.txt", "Old garden notes from last year.\n");
                options = options with { ItemMaxBytes = 100, ProjectMaxBytes = 100, TotalMaxBytes = 100 };
                expected = ArchiveRefusal.TotalCap;
                break;
            case "slug_taken_other_sha":
                _tree.Write("archive/zyggy/quote-2026.txt", "Another quote.\n");
                _tree.Write("archive/zyggy/quote-2026.md", SidecarText(new string('0', 64)));
                expected = ArchiveRefusal.SlugTaken;
                break;
            case "secret_line":
                request = request with { Bytes = "Meeting notes.\nkey AKIAABCDEFGHIJKLMNOP\n"u8.ToArray() };
                expected = ArchiveRefusal.SecretPattern;
                break;
            case "contact_detail":
                request = request with { Name = "Quote from marie@example.com" };
                expected = ArchiveRefusal.ContactDetail;
                break;
            default:
                request = request with { SourceName = "sample.docm", Bytes = await File.ReadAllBytesAsync(Path.Combine(Golden.Directory, "office", "sample.docm"), TestContext.Current.CancellationToken) };
                expected = ArchiveRefusal.OfficeRefused;
                break;
        }

        var before = Disk();

        // Act
        var outcome = await Service(options: options).AddBytesAsync(request, TestContext.Current.CancellationToken);

        // Assert
        outcome.Kind.Should().Be(ArchiveOutcomeKind.Refused);
        outcome.Refusal.Should().Be(expected);
        Disk().Should().Equal(before);
        _git.CallsOf("add").Should().BeEmpty();
        _git.CallsOf("commit").Should().BeEmpty();
    }

    [Fact]
    public async Task AddBytesAsync_SidecarWithSameShaCommitted_AlreadyFiled_NoWriteNoCommit()
    {
        // Arrange: the item filed and committed earlier; git status reports nothing.
        _tree.Write("archive/zyggy/quote-2026.txt", TextItem);
        _tree.Write("archive/zyggy/quote-2026.md", SidecarText(Sha(TextBytes)));
        var before = Disk();

        // Act
        var outcome = await Service().AddBytesAsync(Bytes(), TestContext.Current.CancellationToken);

        // Assert
        outcome.Kind.Should().Be(ArchiveOutcomeKind.AlreadyFiled);
        outcome.Item.Should().Be("archive/zyggy/quote-2026.txt");
        outcome.Sidecar.Should().Be("archive/zyggy/quote-2026.md");
        outcome.Line.Should().BeNull();
        Disk().Should().Equal(before);
        _git.CallsOf("add").Should().BeEmpty();
        _git.CallsOf("commit").Should().BeEmpty();
    }

    [Fact]
    public async Task AddBytesAsync_SidecarWithSameShaUncommitted_AlreadyFiled_CommittedAndPushed()
    {
        // Arrange: an earlier pass wrote both files and its commit failed; git status lists them untracked.
        _tree.Write("archive/zyggy/quote-2026.txt", TextItem);
        _tree.Write("archive/zyggy/quote-2026.md", SidecarText(Sha(TextBytes)));
        _git.On("status", RecordingProcessRunner.Ok("?? acme/alice/archive/zyggy/quote-2026.md\0?? acme/alice/archive/zyggy/quote-2026.txt\0"));
        var before = Disk();

        // Act
        var outcome = await Service().AddBytesAsync(Bytes(), TestContext.Current.CancellationToken);

        // Assert
        outcome.Kind.Should().Be(ArchiveOutcomeKind.AlreadyFiled);
        outcome.Sha.Should().Be("0123456789abcdef0123456789abcdef01234567");
        outcome.Pushed.Should().BeTrue();
        Disk().Should().Equal(before);
        var commit = _git.CallsOf("commit").Single();
        commit.Arguments.Should().Equal("commit", "--only", "-F", "-", "--", "acme/alice/archive/zyggy/quote-2026.md", "acme/alice/archive/zyggy/quote-2026.txt");
        commit.StandardInput.Should().Be(await File.ReadAllTextAsync(Path.Combine(Golden.Directory, "archive", "commit-message-add.txt"), TestContext.Current.CancellationToken));
        _git.CallsOf("push").Should().ContainSingle();
    }

    [Theory]
    [InlineData("not_on_branch")]
    [InlineData("index_lock")]
    [InlineData("commit_failed")]
    public async Task AddBytesAsync_NotOnBranch_IndexLock_GitError_GitErrorNotPushed(string detail)
    {
        // Arrange: not_on_branch stops at the preflight; a held index.lock and a git error stop the commit (files left on disk, spec 37).
        _git.On("symbolic-ref", detail == "not_on_branch" ? RecordingProcessRunner.Fail(1, "") : RecordingProcessRunner.Ok("main\n"));
        var locked = RecordingProcessRunner.Fail(128, Locked);
        if (detail == "index_lock")
        {
            _git.On("commit", locked, locked, locked, locked);
        }
        else if (detail == "commit_failed")
        {
            _git.On("commit", RecordingProcessRunner.Fail(1, "fatal: empty ident name"));
        }

        // Act
        var outcome = await RunAdvancingClockAsync(Service().AddBytesAsync(Bytes(), TestContext.Current.CancellationToken));

        // Assert
        outcome.Kind.Should().Be(ArchiveOutcomeKind.GitError);
        outcome.Detail.Should().Be(detail);
        _git.CallsOf("push").Should().BeEmpty();
    }

    [Fact]
    public async Task AddBytesAsync_PushNotRecoverable_ArchivedPushedFalse()
    {
        // Arrange
        _git.On("push", RecordingProcessRunner.Fail(1, Rejected), RecordingProcessRunner.Fail(1, Rejected));

        // Act
        var outcome = await Service().AddBytesAsync(Bytes(), TestContext.Current.CancellationToken);

        // Assert
        outcome.Kind.Should().Be(ArchiveOutcomeKind.Archived);
        outcome.Pushed.Should().BeFalse();
        outcome.Sha.Should().NotBeNull();
        File.Exists(Item).Should().BeTrue();
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string SidecarText(string sha) =>
        MemoryFileWriter.Render(new ArchiveSidecar("Quote 2026", "Roof repair quote", Slug.Parse("zyggy"), ArchiveMediaType.TextPlain, 68, sha, new DateOnly(2026, 9, 30), "quote.txt", new DateOnly(2026, 9, 30))
            .ToMemoryFile());

    private static SecretPatterns Patterns() => SecretPatterns.Load(Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt")).Patterns!;

    private static ArchiveAddBytes Bytes() => new(Slug.Parse("zyggy"), "Quote 2026", "Roof repair quote", Slug.Parse("quote-2026"), "quote.txt", TextBytes);

    private ArchiveService Service(MemoryTree? tree = null, RecordingProcessRunner? git = null, ArchiveOptions? options = null)
    {
        tree ??= _tree;
        options ??= new ArchiveOptions();
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal) { ["HOME"] = _home };
        var publisher = new MemoryPublisher(new GitClient(git ?? _git, new GitClientOptions(), _clock));
        return new ArchiveService(tree.Paths, Zone, _clock, Patterns(), options, SourceDenyList.Build(environment, options, tree.Paths), publisher, tree.Root);
    }

    private async Task<ArchiveOutcome> RunAdvancingClockAsync(Task<ArchiveOutcome> task)
    {
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
