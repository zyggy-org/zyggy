using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Git;
using Zyggy.Core.Memory;
using Zyggy.Core.Processes;
using Zyggy.Core.Registry;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Registry;

public sealed class DiscoveryScannerTests : IDisposable
{
    private readonly ScanTree _tree = new();
    private readonly RecordingProcessRunner _processes = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 7, 9, 0, 0, TimeSpan.Zero));
    private readonly RecordingLogger<DiscoveryScanner> _log = new();
    private readonly DiscoveryOptions _options = new();

    internal static SecretPatterns Patterns { get; } =
        SecretPatterns.Load(Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt")).Patterns!;

    public void Dispose() => _tree.Dispose();

    private DiscoveryScanner Scanner(Func<string, IEnumerable<DirectoryInfo>>? listChildren = null) =>
        new(new GitClient(_processes, new GitClientOptions(), _clock), Patterns, _options, _clock, _log, listChildren);

    private static Task<ScanResult> ScanAsync(DiscoveryScanner scanner, params string[] roots) =>
        scanner.ScanAsync(roots, DiscoveryDetail.Full, TestContext.Current.CancellationToken);

    private static IEnumerable<string> Names(ScanResult result) => result.Projects.Select(p => p.Name);

    [Fact]
    public async Task ScanAsync_Ac1Tree_OnlyDirectChildrenWithClaude()
    {
        // Arrange
        string a = _tree.Root("a");
        string b = _tree.Root("b");
        string calizr = _tree.Project(a, "calizr");
        string notes = _tree.Project(b, "notes", git: false);
        _tree.Folder(a, "plain");
        _tree.Project(a, ".cache");
        _tree.File(a, "readme.txt", "x");
        _tree.Project(Path.Combine(a, "org"), "repo");

        // Act
        ScanResult result = await ScanAsync(Scanner(), a, b);

        // Assert
        Names(result).Should().Equal("calizr", "notes");
        result.Projects[0].Path.Should().Be(ScanTree.Published(calizr));
        result.Projects[0].DefaultBranch.Should().NotBeNull();
        result.Projects[1].Path.Should().Be(ScanTree.Published(notes));
        result.Projects[1].DefaultBranch.Should().BeNull("a folder without .git has no branch");
        _processes.Calls.Select(c => c.WorkingDirectory).Distinct().Should().Equal(Path.GetFullPath(calizr));
        result.Notices.Should().BeEmpty();
        result.EffectiveDetail.Should().Be(DiscoveryDetail.Full);
    }

    [Fact]
    public async Task ScanAsync_LinkedProject_NeverFollowed()
    {
        // Arrange
        string a = _tree.Root("a");
        string elsewhere = _tree.Root("elsewhere");
        string target = _tree.Project(elsewhere, "secret-project");
        bool linked = _tree.Link(Path.Combine(a, "linked"), target);
        Assert.SkipUnless(linked, "symbolic links need a privilege on this machine");

        // Act
        ScanResult result = await ScanAsync(Scanner(), a);

        // Assert
        result.Projects.Should().BeEmpty();
        _processes.Calls.Should().NotContain(c => c.WorkingDirectory.Contains("secret-project", StringComparison.Ordinal)
            || c.WorkingDirectory.Contains("linked", StringComparison.Ordinal));
    }

    public static TheoryData<string, string?> BranchLadder() => new()
    {
        { "origin-head", "main" },
        { "local-main", "main" },
        { "local-master", "master" },
        { "current", "feature/x" },
        { "detached", null },
    };

    [Theory]
    [MemberData(nameof(BranchLadder))]
    public async Task ScanAsync_DefaultBranchLadder(string scenario, string? expected)
    {
        // Arrange
        string a = _tree.Root("a");
        _tree.Project(a, "calizr");
        _processes.Hook = spec => Answer(scenario, spec);

        // Act
        ScanResult result = await ScanAsync(Scanner(), a);

        // Assert
        result.Projects.Should().ContainSingle().Which.DefaultBranch.Should().Be(expected);
    }

    [Fact]
    public async Task ScanAsync_OriginHeadSet_FeatureCheckout_StillMain()
    {
        // Arrange: origin/HEAD names main while a feature branch is checked out
        string a = _tree.Root("a");
        _tree.Project(a, "calizr");
        _processes.Hook = spec => string.Join(' ', spec.Arguments) switch
        {
            "symbolic-ref -q --short refs/remotes/origin/HEAD" => RecordingProcessRunner.Ok("origin/main\n"),
            "symbolic-ref --short -q HEAD" => RecordingProcessRunner.Ok("feature/y\n"),
            _ => RecordingProcessRunner.Fail(1, ""),
        };

        // Act
        ScanResult first = await ScanAsync(Scanner(), a);
        ScanResult second = await ScanAsync(Scanner(), a);

        // Assert
        first.Projects[0].DefaultBranch.Should().Be("main");
        second.Projects[0].Should().Be(first.Projects[0]);
    }

    [Fact]
    public async Task ScanAsync_UnusableNames_SkippedWithNoticeNamingFolder()
    {
        // Arrange
        string a = _tree.Root("a");
        string dashes = _tree.Project(a, "--");
        string umlauts = _tree.Project(a, "ÄÖ");
        _tree.Project(a, "calizr");
        _tree.Project(a, "My_App");
        _tree.Project(a, "Client Portal.v2");

        // Act
        ScanResult result = await ScanAsync(Scanner(), a);

        // Assert
        Names(result).Should().Equal("calizr", "client-portal-v2", "my-app");
        result.Notices.Should().HaveCount(2).And.OnlyContain(n => n.Kind == ScanNoticeKind.NameUnusable);
        result.Notices.Select(n => n.Subject).Should().BeEquivalentTo(ScanTree.Published(dashes), ScanTree.Published(umlauts));
    }

    [Fact]
    public async Task ScanAsync_SameNameUnderTwoRoots_NeitherPublishedOneNotice()
    {
        // Arrange
        string a = _tree.Root("a");
        string b = _tree.Root("b");
        string first = _tree.Project(a, "calizr");
        string second = _tree.Project(b, "Calizr");

        // Act
        ScanResult result = await ScanAsync(Scanner(), a, b);

        // Assert
        result.Projects.Should().BeEmpty();
        ScanNotice notice = result.Notices.Should().ContainSingle().Subject;
        notice.Kind.Should().Be(ScanNoticeKind.DuplicateName);
        notice.Subject.Should().Be("calizr");
        notice.Detail.Should().Contain(ScanTree.Published(first)).And.Contain(ScanTree.Published(second));
    }

    [Fact]
    public async Task ScanAsync_MissingAndUnreadableRoot_GoodRootStillScanned()
    {
        // Arrange
        string good = _tree.Root("good");
        string locked = _tree.Root("locked");
        string missing = Path.Combine(_tree.Base, "missing");
        _tree.Project(good, "calizr");
        DiscoveryScanner scanner = Scanner(root => string.Equals(root, Path.GetFullPath(locked), StringComparison.Ordinal)
            ? throw new UnauthorizedAccessException("denied")
            : new DirectoryInfo(root).EnumerateDirectories());

        // Act
        ScanResult result = await ScanAsync(scanner, good, locked, missing);

        // Assert
        Names(result).Should().Equal("calizr");
        result.Notices.Select(n => n.Kind).Should().BeEquivalentTo([ScanNoticeKind.RootUnreadable, ScanNoticeKind.RootMissing]);
        result.Notices.Single(n => n.Kind == ScanNoticeKind.RootMissing).Subject.Should().Be(ScanTree.Published(missing));
    }

    [Fact]
    public async Task ScanAsync_MoreProjectsThanCap_CappedWithNotice()
    {
        // Arrange
        _options.MaxProjects = 3;
        string a = _tree.Root("a");
        foreach (string folder in new[] { "d", "a1", "c", "b" })
        {
            _tree.Project(a, folder, git: false);
        }

        // Act
        ScanResult result = await ScanAsync(Scanner(), a);

        // Assert
        Names(result).Should().Equal("a1", "b", "c");
        result.Notices.Should().ContainSingle(n => n.Kind == ScanNoticeKind.ProjectCapReached);
    }

    [Fact]
    public async Task ScanAsync_WalkLongerThanThreshold_SlowScanNotice()
    {
        // Arrange
        string a = _tree.Root("a");
        _tree.Project(a, "calizr", git: false);
        DiscoveryScanner scanner = Scanner(root =>
        {
            _clock.Advance(TimeSpan.FromSeconds(31));
            return new DirectoryInfo(root).EnumerateDirectories();
        });

        // Act
        ScanResult result = await ScanAsync(scanner, a);

        // Assert
        result.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(31));
        ScanNotice notice = result.Notices.Should().ContainSingle(n => n.Kind == ScanNoticeKind.SlowScan).Subject;
        notice.Detail.Should().Contain("31");
    }

    [Fact]
    public async Task ScanAsync_NamesDetail_OnlyNameAndDefaultBranch_NothingUnderClaudeRead()
    {
        // Arrange
        string a = _tree.Root("a");
        string project = _tree.Project(a, "calizr");
        string agent = _tree.File(project, Path.Combine(".claude", "agents", "x.md"), "---\ndescription: hidden\n---\n");
        string claudeMd = _tree.File(project, "CLAUDE.md", "A summary line.\n");
        using var agentLock = new FileStream(agent, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var claudeLock = new FileStream(claudeMd, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        // Act
        ScanResult result = await Scanner().ScanAsync([a], DiscoveryDetail.Names, TestContext.Current.CancellationToken);

        // Assert
        RegistryProject only = result.Projects.Should().ContainSingle().Subject;
        only.Name.Should().Be("calizr");
        only.DefaultBranch.Should().Be("main");
        only.Path.Should().BeNull();
        only.Summary.Should().BeNull();
        only.Agents.Should().BeEmpty();
        only.Skills.Should().BeEmpty();
        result.Notices.Should().BeEmpty();
        result.EffectiveDetail.Should().Be(DiscoveryDetail.Names);
    }

    [Fact]
    public async Task ScanAsync_DuplicateRootsAndNestedRoot_ScannedOnce()
    {
        // Arrange
        string a = _tree.Root("a");
        _tree.Project(a, "calizr", git: false);
        string nested = _tree.Folder(a, "inner");
        _tree.Project(nested, "deep", git: false);

        // Act
        ScanResult result = await ScanAsync(Scanner(), a, a + Path.DirectorySeparatorChar, nested);

        // Assert
        Names(result).Should().Equal("calizr");
        result.Notices.Should().BeEmpty();
    }

    [Fact]
    public async Task ScanAsync_RootIsItselfAProject_NotListed()
    {
        // Arrange
        string a = _tree.Root("a");
        Directory.CreateDirectory(Path.Combine(a, ".claude"));
        _tree.Project(a, "calizr", git: false);

        // Act
        ScanResult result = await ScanAsync(Scanner(), a);

        // Assert
        Names(result).Should().Equal("calizr");
    }

    private static ProcessResult Answer(string scenario, ProcessSpec spec)
    {
        string command = string.Join(' ', spec.Arguments);
        return (scenario, command) switch
        {
            ("origin-head", "symbolic-ref -q --short refs/remotes/origin/HEAD") => RecordingProcessRunner.Ok("origin/main\n"),
            ("local-main", "rev-parse --verify -q refs/heads/main") => RecordingProcessRunner.Ok("0123\n"),
            ("local-master", "rev-parse --verify -q refs/heads/master") => RecordingProcessRunner.Ok("0123\n"),
            ("current", "symbolic-ref --short -q HEAD") => RecordingProcessRunner.Ok("feature/x\n"),
            _ => RecordingProcessRunner.Fail(1, ""),
        };
    }
}
