using System.Text;
using System.Text.RegularExpressions;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Cli;

/// <summary>Spec 12 AC-18..AC-21: <c>zyggy context --flush</c> at <c>SessionEnd</c> ships the session's spool to the bare repository as one signed envelope.</summary>
public sealed partial class ContextFlushCommandTests : IAsyncLifetime
{
    private const string AwsKey = "AKIAABCDEFGHIJKLMNOP";

    private static readonly string[] ThreeLines =
    [
        "- [observed] 2026-10-28 (project:calizr) [session 0b7c3d1e 10:40Z]: one",
        "- [observed] 2026-10-28 (project:calizr) [session 0b7c3d1e 10:41Z]: two",
        "- [observed] 2026-10-28 (project:calizr) [session 0b7c3d1e 10:42Z]: three",
    ];

    private CliHarness _harness = null!;

    public async ValueTask InitializeAsync() => _harness = await CliHarness.CreateAsync(CancellationToken.None);

    public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

    private string SpoolDirectory => ContextSpool.DirectoryFor(_harness.NodeDirOf("home-laptop"), CliHarness.Acme);

    private string SpoolPath(string session = HookJson.SessionId) => Path.Combine(SpoolDirectory, session + ".md");

    private Task<(int Exit, VerbConsole Console)> FlushAsync(string? stdin = HookJson.Fixture, string? nodeJson = null, IReadOnlyDictionary<string, string?>? env = null, bool withPatterns = true, bool verbose = false, CancellationToken ct = default) =>
        _harness.RunAsync(verbose ? ["context", "--flush", "--verbose"] : ["context", "--flush"], nodeJson ?? _harness.HomeLaptopNodeJson, stdin: stdin is null ? null : HookJson.SessionEnd(), extraEnvironment: env, withPatterns: withPatterns, ct: ct);

    private void Seed(string session, string[] lines, int ageMinutes = 0, string scope = "project:calizr")
    {
        Directory.CreateDirectory(SpoolDirectory);
        File.WriteAllBytes(SpoolPath(session), new UTF8Encoding(false).GetBytes($"scope: {scope}\n" + string.Concat(lines.Select(l => l + "\n"))));
        File.SetLastWriteTimeUtc(SpoolPath(session), DateTime.UtcNow.AddMinutes(-ageMinutes));
    }

    [Fact]
    public async Task Flush_SessionEndJsonAndThreeLineSpool_ExitZeroOneLine_EnvelopeOnMainWithScopeAndLines_SpoolDeleted()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Seed(HookJson.SessionId, ThreeLines);

        // Act
        var (exit, console) = await FlushAsync(ct: ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        console.Stderr.Should().BeEmpty();
        (EnvelopeId id, string path) = Published(console.Stdout);
        console.Stdout.Should().Be($"context {id.Value} to central ({path})\n");
        Model context = await _harness.ParseOnMainAsync(path, ct);
        context.Context!.Scope.Should().Be(ContextScope.Parse("project:calizr"));
        context.BodyText.Should().Be(string.Concat(ThreeLines.Select(l => l + "\n")));
        context.Header.From.Should().Be(CliHarness.HomeLaptop);
        context.Header.To.Should().Be(MachineName.Central);
        File.Exists(SpoolPath()).Should().BeFalse();
        (await _harness.PorcelainAsync(_harness.Bus.CloneDir, ct)).Should().BeEmpty();
        (await _harness.Bus.LastCommitSubjectAsync(ct)).Should().Be($"acme context {id.Value} home-laptop→central");
    }

    [Fact]
    public async Task Flush_ThenVerifyOnCentral_Ok()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Seed(HookJson.SessionId, ThreeLines);
        var (exit, console) = await FlushAsync(ct: ct);
        exit.Should().Be(0, console.Stderr);
        (_, string path) = Published(console.Stdout);
        await _harness.Bus.RunGitAsync(_harness.Bus.SenderDir, ["pull", "--rebase", "origin", "main"], ct);

        // Act
        var (verifyExit, verify) = await _harness.RunAsync(["verify", "--verbose"], _harness.CentralNodeJson, ct: ct);

        // Assert
        verifyExit.Should().Be(0, verify.Stderr + verify.Stdout);
        verify.Stdout.Should().Contain("OK " + path).And.NotContain("FAIL");
    }

    [Fact]
    public async Task Flush_StaleSpoolShipsToo_FreshSpoolStays()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Seed(HookJson.SessionId, ThreeLines);
        Seed("stale-session", ["- stale"], ageMinutes: 61);
        Seed("fresh-session", ["- fresh"], ageMinutes: 5);

        // Act
        var (exit, console) = await FlushAsync(ct: ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        string[] lines = console.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(2);
        foreach (string line in lines)
        {
            (await _harness.Bus.ExistsOnMainAsync(Published(line).Path, ct)).Should().BeTrue(line);
        }

        File.Exists(SpoolPath()).Should().BeFalse();
        File.Exists(SpoolPath("stale-session")).Should().BeFalse();
        File.Exists(SpoolPath("fresh-session")).Should().BeTrue();
    }

    [Fact]
    public async Task Flush_NothingDue_ExitZeroNothing()
    {
        // Act: no directory, then an empty directory, then only a fresh spool of another session
        var ct = TestContext.Current.CancellationToken;
        var (exit1, c1) = await FlushAsync(ct: ct);
        Directory.CreateDirectory(SpoolDirectory);
        var (exit2, c2) = await FlushAsync(ct: ct);
        Seed("fresh-session", ["- fresh"], ageMinutes: 5);
        var (exit3, c3) = await FlushAsync(ct: ct);

        // Assert
        foreach (var (exit, console) in new[] { (exit1, c1), (exit2, c2), (exit3, c3) })
        {
            exit.Should().Be(0, console.Stderr);
            console.Stdout.Should().BeEmpty();
            console.Stderr.Should().BeEmpty();
        }

        File.Exists(SpoolPath("fresh-session")).Should().BeTrue();
    }

    [Fact]
    public async Task Flush_NoStdinRedirected_StaleOnly()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Seed(HookJson.SessionId, ThreeLines);
        Seed("stale-session", ["- stale"], ageMinutes: 61);

        // Act
        var (exit, console) = await FlushAsync(stdin: null, ct: ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        console.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(1);
        File.Exists(SpoolPath()).Should().BeTrue("the fresh spool of the fixture session is not named without stdin");
        File.Exists(SpoolPath("stale-session")).Should().BeFalse();
    }

    [Fact]
    public async Task Flush_PullFails_ExitSixSpoolKeptByteIdentical()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Seed(HookJson.SessionId, ThreeLines);
        byte[] before = await File.ReadAllBytesAsync(SpoolPath(), ct);
        _harness.Bus.DeleteBare();

        // Act
        var (exit, console) = await FlushAsync(ct: ct);

        // Assert
        exit.Should().Be(6);
        console.Stderr.Should().StartWith("zyggy: context failed at pull:");
        console.Stdout.Should().BeEmpty();
        (await File.ReadAllBytesAsync(SpoolPath(), ct)).Should().Equal(before);
        SecretSweep.AssertAbsent([console.Stderr]);
    }

    [Fact]
    public async Task Flush_EveryPushRefused_ExitSevenSuffix_SpoolDeleted_NextWritePushesIt()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Seed(HookJson.SessionId, ThreeLines);
        _harness.Hooks.Override = spec => HookedProcessRunner.SubCommand(spec) == "push"
            ? new Zyggy.Core.Processes.ProcessResult(1, string.Empty, "! [rejected] main -> main (fetch first)", false, false, false, TimeSpan.Zero)
            : null;
        int commits = await _harness.CommitCountOnMainAsync(ct);

        // Act
        var (exit, console) = await FlushAsync(ct: ct);

        // Assert
        exit.Should().Be(7);
        console.Stdout.Should().EndWith(" (committed locally, not yet pushed: the next bus write on this machine pushes it)\n");
        (_, string path) = Published(console.Stdout);
        File.Exists(SpoolPath()).Should().BeFalse();
        (await _harness.CommitCountOnMainAsync(ct)).Should().Be(commits);

        // Act: the next bus write pushes it
        _harness.Hooks.Override = null;
        var (nextExit, next) = await _harness.RunAsync(["context", "Another fact"], _harness.HomeLaptopNodeJson, ct: ct);

        // Assert
        nextExit.Should().Be(0, next.Stderr);
        (await _harness.Bus.ExistsOnMainAsync(path, ct)).Should().BeTrue();
        (await _harness.Bus.ExistsOnMainAsync(Published(next.Stdout).Path, ct)).Should().BeTrue();
    }

    [Fact]
    public async Task Flush_HandEditedSecretLine_DroppedOneStderrLine_RestShips_ValueNowhere()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Seed(HookJson.SessionId, [ThreeLines[0], ThreeLines[1], "- the key is " + AwsKey]);

        // Act
        var (exit, console) = await FlushAsync(ct: ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        console.Stderr.Should().Be("context: line dropped (pattern aws-access-key)\n");
        (_, string path) = Published(console.Stdout);
        Model context = await _harness.ParseOnMainAsync(path, ct);
        context.BodyText.Should().Be(ThreeLines[0] + "\n" + ThreeLines[1] + "\n");
        string log = await _harness.Bus.RunGitAsync(_harness.Bus.BareDir, ["log", "-p", "--all"], ct);
        var texts = new List<string?> { console.Stdout, console.Stderr, log, Encoding.UTF8.GetString(await _harness.Bus.ShowBytesAsync(path, ct)) };
        texts.AddRange(_harness.Hooks.Calls.SelectMany(SecretSweep.TextsOf));
        texts.Should().NotContain(t => t != null && t.Contains(AwsKey, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Flush_SpoolOfOnlySecretLines_NothingShippedSpoolDeleted_ExitZero()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Seed(HookJson.SessionId, ["- the key is " + AwsKey]);
        int commits = await _harness.CommitCountOnMainAsync(ct);

        // Act
        var (exit, console) = await FlushAsync(ct: ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        console.Stdout.Should().BeEmpty();
        console.Stderr.Should().Be("context: line dropped (pattern aws-access-key)\n");
        File.Exists(SpoolPath()).Should().BeFalse();
        (await _harness.CommitCountOnMainAsync(ct)).Should().Be(commits);
    }

    [Fact]
    public async Task Flush_HooksOff_ExitZeroNothing_SpoolUntouched()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Seed(HookJson.SessionId, ThreeLines);
        var env = new Dictionary<string, string?>(StringComparer.Ordinal) { ["ZYGGY_HOOKS"] = "off" };

        // Act
        var (exit, console) = await FlushAsync(env: env, withPatterns: false, ct: ct);

        // Assert
        exit.Should().Be(0);
        console.Stdout.Should().BeEmpty();
        console.Stderr.Should().BeEmpty();
        File.Exists(SpoolPath()).Should().BeTrue();
        _harness.Hooks.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Flush_PatternsUnresolvable_ExitThree_SpoolUntouched()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Seed(HookJson.SessionId, ThreeLines);
        var env = new Dictionary<string, string?>(StringComparer.Ordinal) { ["CLAUDE_PROJECT_DIR"] = null, ["ZYGGY_INSTANCE_DIR"] = null };

        // Act
        var (exit, console) = await FlushAsync(env: env, withPatterns: false, ct: ct);

        // Assert
        exit.Should().Be(3);
        console.Stderr.Should().StartWith("zyggy: configuration error: ZYGGY_SECRET_PATTERNS is not set");
        File.Exists(SpoolPath()).Should().BeTrue();
        _harness.Hooks.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Flush_SignWithMissing_ExitThree()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string nodeJson = _harness.WriteNodeJson(CliHarness.HomeLaptop, _harness.Bus.CloneDir, "laptop-nosign", omitSignWith: true);

        // Act
        var (exit, console) = await FlushAsync(nodeJson: nodeJson, ct: ct);

        // Assert
        exit.Should().Be(3);
        console.Stderr.Should().Be("zyggy: configuration error: signWith missing\n");
    }

    [Fact]
    public async Task Flush_CentralNodeJson_ExitTwo()
    {
        // Act
        var ct = TestContext.Current.CancellationToken;
        var (exit, console) = await FlushAsync(nodeJson: _harness.CentralNodeJson, ct: ct);

        // Assert
        exit.Should().Be(2);
        console.Stderr.Should().Be("zyggy: to: this machine is central; use zyggy memory remember\n");
        _harness.Hooks.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Flush_StopHookThenFlush_EndToEnd_OneEnvelopePerSession()
    {
        // Arrange: two turns of the fixture session, then its SessionEnd
        var ct = TestContext.Current.CancellationToken;
        var (s1, c1) = await _harness.RunAsync(["context", "--stop-hook"], _harness.HomeLaptopNodeJson, stdin: HookJson.Stop("First turn.", cwd: "/home/alice/src/Calizr"), ct: ct);
        var (s2, c2) = await _harness.RunAsync(["context", "--stop-hook"], _harness.HomeLaptopNodeJson, stdin: HookJson.Stop("Second turn.", cwd: "/home/alice/src/Calizr"), ct: ct);
        s1.Should().Be(0, c1.Stderr);
        s2.Should().Be(0, c2.Stderr);

        // Act
        var (exit, console) = await FlushAsync(ct: ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        (_, string path) = Published(console.Stdout);
        Model context = await _harness.ParseOnMainAsync(path, ct);
        context.Context!.Scope.Should().Be(ContextScope.Parse("project:calizr"));
        string[] body = context.BodyText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        body.Should().HaveCount(2);
        body[0].Should().MatchRegex(@"^- \[observed\] \d{4}-\d{2}-\d{2} \(project:calizr\) \[session 0b7c3d1e \d{2}:\d{2}Z\]: First turn\.$");
        body[1].Should().EndWith(": Second turn.");
        File.Exists(SpoolPath()).Should().BeFalse();
        await _harness.Bus.RunGitAsync(_harness.Bus.SenderDir, ["pull", "--rebase", "origin", "main"], ct);
        var (verifyExit, verify) = await _harness.RunAsync(["verify", "--verbose"], _harness.CentralNodeJson, ct: ct);
        verifyExit.Should().Be(0, verify.Stderr);
        verify.Stdout.Should().Contain("OK " + path);
    }

    [Fact]
    public async Task Flush_EveryOutputNoKeyNoDroppedText()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Seed(HookJson.SessionId, [ThreeLines[0], "- token " + AwsKey]);

        // Act
        var (exit, console) = await FlushAsync(verbose: true, ct: ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        var texts = new List<string?> { console.Stdout, console.Stderr };
        texts.AddRange(_harness.Hooks.Calls.SelectMany(SecretSweep.TextsOf));
        SecretSweep.AssertAbsent(texts);
        texts.Should().NotContain(t => t != null && t.Contains(AwsKey, StringComparison.Ordinal));
    }

    private static (EnvelopeId Id, string Path) Published(string line)
    {
        Match match = PublishedLine().Match(line);
        match.Success.Should().BeTrue(line);
        return (EnvelopeId.Parse(match.Groups["id"].Value), match.Groups["path"].Value);
    }

    [GeneratedRegex(@"^context (?<id>[0-9A-HJKMNP-TV-Z]{26}) to central \((?<path>\S+)\)")]
    private static partial Regex PublishedLine();
}
