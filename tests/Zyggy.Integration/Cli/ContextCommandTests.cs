using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Cli;

/// <summary>
/// Spec 12 AC-1..AC-13, AC-22, AC-23: <c>zyggy context</c> in process from <c>home-laptop</c>'s <c>node.json</c> over the node clone, on the
/// bare repository — a stated fact lands as a signed context envelope in the sender's own folder, addressed to central (D1).
/// </summary>
public sealed partial class ContextCommandTests : IAsyncLifetime
{
    private const string Fact = "The staging database is restored from Sunday's snapshot";

    private const string AwsKey = "AKIAABCDEFGHIJKLMNOP";

    private static readonly string[] Section4ContextKeys = ["created", "from", "id", "key_id", "priority", "schema", "scope", "sig", "tenant", "to", "type"];

    private CliHarness _harness = null!;

    public async ValueTask InitializeAsync() => _harness = await CliHarness.CreateAsync(CancellationToken.None);

    public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

    private static string Today => DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    [Fact]
    public async Task Context_ScopeProjectFact_ExitZeroTwoLinesFileOnMainSubjectCleanClone()
    {
        // Act
        var ct = TestContext.Current.CancellationToken;
        var (exit, console) = await _harness.RunAsync(["context", "--scope", "project:calizr", Fact], _harness.HomeLaptopNodeJson, ct: ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        (EnvelopeId id, string path) = Published(console.Stdout);
        path.Should().Be(BusPaths.EnvelopeFile(BusPaths.Context(CliHarness.Acme, CliHarness.HomeLaptop), id));
        console.Stdout.Should().Be($"context {id.Value} to central ({path})\n- [stated] {Today} (project:calizr): {Fact}\n");
        console.Stderr.Should().BeEmpty();
        (await _harness.Bus.ExistsOnMainAsync(path, ct)).Should().BeTrue();
        (await _harness.Bus.LastCommitSubjectAsync(ct)).Should().Be($"acme context {id.Value} home-laptop→central");
        (await _harness.PorcelainAsync(_harness.Bus.CloneDir, ct)).Should().BeEmpty();
        string log = await _harness.Bus.RunGitAsync(_harness.Bus.BareDir, ["log", "-p", "-1", "main"], ct);
        SecretSweep.AssertAbsent([console.Stdout, console.Stderr, Encoding.UTF8.GetString(await _harness.Bus.ShowBytesAsync(path, ct)), log]);
    }

    [Fact]
    public async Task Context_FileOnMain_Section4ContextFieldsAndBodyExactly()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        DateTimeOffset before = DateTimeOffset.UtcNow;

        // Act
        var (exit, console) = await _harness.RunAsync(["context", "--scope", "project:calizr", Fact], _harness.HomeLaptopNodeJson, ct: ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        (EnvelopeId id, string path) = Published(console.Stdout);
        Model context = await _harness.ParseOnMainAsync(path, ct);
        context.Schema.Should().Be(1);
        context.Type.Should().Be(EnvelopeType.Context);
        context.Header.Tenant.Should().Be(CliHarness.Acme);
        context.Header.From.Should().Be(CliHarness.HomeLaptop);
        context.Header.To.Should().Be(MachineName.Central);
        context.Header.InReplyTo.Should().BeNull();
        context.Header.Priority.Should().Be(EnvelopePriority.Normal);
        context.Context!.Scope.Should().Be(ContextScope.Parse("project:calizr"));
        context.KeyId.Should().Be(TestKeys.Acme1);
        context.Signature.Should().MatchRegex("^hmac-sha256:[0-9a-f]{64}$");
        context.Header.Created.Should().BeOnOrAfter(before.AddSeconds(-1)).And.BeOnOrBefore(DateTimeOffset.UtcNow.AddSeconds(5));
        context.Header.Id.Should().Be(id);
        path.Should().EndWith("/" + id.Value + ".md");
        context.Body.ToArray().Should().Equal(Encoding.UTF8.GetBytes(console.Stdout.Split('\n')[1] + "\n"));
        context.FrontMatter.Entries.Keys.Order(StringComparer.Ordinal).Should().Equal(Section4ContextKeys);
    }

    [Fact]
    public async Task Context_ThenVerifyOnCentral_OkAndExitZero()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (exit, console) = await _harness.RunAsync(["context", Fact], _harness.HomeLaptopNodeJson, ct: ct);
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
    public async Task Context_ThenStatusOnCentral_CountsOneContextForHomeLaptop()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (exit, console) = await _harness.RunAsync(["context", Fact], _harness.HomeLaptopNodeJson, ct: ct);
        exit.Should().Be(0, console.Stderr);
        await _harness.Bus.RunGitAsync(_harness.Bus.SenderDir, ["pull", "--rebase", "origin", "main"], ct);

        // Act
        var (_, status) = await _harness.RunAsync(["status"], _harness.CentralNodeJson, ct: ct);

        // Assert
        string[] lines = status.Stdout.Split('\n');
        lines.Should().Contain("        machine       queued  claimed  rejected  reports  context");
        string row = lines.Single(l => l.StartsWith("        home-laptop", StringComparison.Ordinal));
        row.Split(' ', StringSplitOptions.RemoveEmptyEntries).Should().Equal("home-laptop", "0", "0", "0", "0", "1");
    }

    [Fact]
    public async Task Context_NoScope_GeneralNoHint_ScopeMachine_HintMachine()
    {
        // Act
        var ct = TestContext.Current.CancellationToken;
        var (exitGeneral, general) = await _harness.RunAsync(["context", Fact], _harness.HomeLaptopNodeJson, ct: ct);
        var (exitMachine, machine) = await _harness.RunAsync(["context", "--scope", "machine", Fact], _harness.HomeLaptopNodeJson, ct: ct);

        // Assert
        exitGeneral.Should().Be(0, general.Stderr);
        exitMachine.Should().Be(0, machine.Stderr);
        Model first = await _harness.ParseOnMainAsync(Published(general.Stdout).Path, ct);
        Model second = await _harness.ParseOnMainAsync(Published(machine.Stdout).Path, ct);
        first.Context!.Scope.Should().Be(ContextScope.Parse("general"));
        first.BodyText.Should().Be($"- [stated] {Today}: {Fact}\n");
        second.Context!.Scope.Should().Be(ContextScope.Parse("machine"));
        second.BodyText.Should().Be($"- [stated] {Today} (machine): {Fact}\n");
    }

    [Theory]
    [InlineData("project:Has Space", "zyggy: scope: invalid project name in --scope\n")]
    [InlineData("people/marie", "zyggy: scope: --scope must be general, project:<name> or machine\n")]
    public async Task Context_InvalidScope_ExitTwoOneLineNothingWritten(string scope, string expected)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        int commits = await _harness.CommitCountOnMainAsync(ct);

        // Act
        var (exit, console) = await _harness.RunAsync(["context", "--scope", scope, Fact], _harness.HomeLaptopNodeJson, ct: ct);

        // Assert
        exit.Should().Be(2);
        console.Stderr.Should().Be(expected);
        console.Stdout.Should().BeEmpty();
        (await _harness.CommitCountOnMainAsync(ct)).Should().Be(commits);
        (await _harness.PorcelainAsync(_harness.Bus.CloneDir, ct)).Should().BeEmpty();
        _harness.Hooks.Calls.Select(HookedProcessRunner.SubCommand).Should().NotContain(["add", "commit", "push"]);
    }

    [Theory]
    [InlineData("", "zyggy: fact: empty fact\n")]
    [InlineData(" \t ", "zyggy: fact: empty fact\n")]
    [InlineData("aaaa", "zyggy: fact: fact longer than 1000 characters\n")]
    public async Task Context_FactEmptyWhitespaceOrOver1000_ExitTwo(string fact, string expected)
    {
        // Arrange: the four-a fact stands for 1,001 characters
        var ct = TestContext.Current.CancellationToken;
        string text = fact == "aaaa" ? new string('a', 1001) : fact;

        // Act
        var (exit, console) = await _harness.RunAsync(["context", text], _harness.HomeLaptopNodeJson, ct: ct);

        // Assert
        exit.Should().Be(2);
        console.Stderr.Should().Be(expected);
        console.Stdout.Should().BeEmpty();
    }

    [Fact]
    public async Task Context_FactOnStdin_IdenticalToPositional()
    {
        // Act
        var ct = TestContext.Current.CancellationToken;
        var (exit, console) = await _harness.RunAsync(["context", "--scope", "project:calizr"], _harness.HomeLaptopNodeJson, stdin: Fact + "\r\n  second line\n", ct: ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        Model context = await _harness.ParseOnMainAsync(Published(console.Stdout).Path, ct);
        context.BodyText.Should().Be($"- [stated] {Today} (project:calizr): {Fact} second line\n");
    }

    [Fact]
    public async Task Context_FactBothPositionalAndStdin_ExitTwo()
    {
        // Act
        var ct = TestContext.Current.CancellationToken;
        var (exit, console) = await _harness.RunAsync(["context", Fact], _harness.HomeLaptopNodeJson, stdin: "another\n", ct: ct);

        // Assert
        exit.Should().Be(2);
        console.Stderr.Should().Be("zyggy: fact: given both as an argument and on stdin\n");
    }

    [Fact]
    public async Task Context_NoFactNoStdin_ExitTwo()
    {
        // Act
        var ct = TestContext.Current.CancellationToken;
        var (exit, console) = await _harness.RunAsync(["context"], _harness.HomeLaptopNodeJson, ct: ct);

        // Assert
        exit.Should().Be(2);
        console.Stderr.Should().Be("zyggy: fact: give the fact as the last argument or on stdin\n");
    }

    [Fact]
    public async Task Context_SecretShapedFact_ExitFiveNamesPattern_FactNowhere_CheckoutUnchanged()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        int commits = await _harness.CommitCountOnMainAsync(ct);
        string porcelain = await _harness.PorcelainAsync(_harness.Bus.CloneDir, ct);

        // Act
        var (exit, console) = await _harness.RunAsync(["context", "the key is " + AwsKey], _harness.HomeLaptopNodeJson, ct: ct);

        // Assert
        exit.Should().Be(5);
        console.Stderr.Should().Be("zyggy: refused: matches secret pattern aws-access-key\n");
        console.Stdout.Should().BeEmpty();
        string log = await _harness.Bus.RunGitAsync(_harness.Bus.BareDir, ["log", "-p", "--all"], ct);
        var texts = new List<string?> { console.Stdout, console.Stderr, log };
        texts.AddRange(Directory.EnumerateFiles(_harness.Bus.CloneDir, "*", SearchOption.AllDirectories).Where(f => !f.Contains(".git", StringComparison.Ordinal)).Select(File.ReadAllText));
        texts.AddRange(_harness.Hooks.Calls.SelectMany(SecretSweep.TextsOf));
        texts.Should().NotContain(t => t != null && t.Contains(AwsKey, StringComparison.Ordinal));
        (await _harness.CommitCountOnMainAsync(ct)).Should().Be(commits);
        (await _harness.PorcelainAsync(_harness.Bus.CloneDir, ct)).Should().Be(porcelain);
    }

    [Theory]
    [InlineData("unset", "zyggy: configuration error: ZYGGY_SECRET_PATTERNS is not set (and no ZYGGY_INSTANCE_DIR or CLAUDE_PROJECT_DIR to derive it from)\n")]
    [InlineData("missing", " is missing\n")]
    [InlineData("malformed", ": malformed line 'no-tab-here'\n")]
    public async Task Context_PatternsUnresolvable_ExitThreeFailClosed(string kind, string expected)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string path = _harness.Scratch.File("patterns-" + kind + ".txt");
        if (kind == "malformed")
        {
            await File.WriteAllTextAsync(path, "no-tab-here\n", ct);
        }

        var env = new Dictionary<string, string?>(StringComparer.Ordinal) { ["CLAUDE_PROJECT_DIR"] = null, ["ZYGGY_INSTANCE_DIR"] = null };
        if (kind != "unset")
        {
            env["ZYGGY_SECRET_PATTERNS"] = path;
        }

        // Act
        var (exit, console) = await _harness.RunAsync(["context", Fact], _harness.HomeLaptopNodeJson, extraEnvironment: env, withPatterns: false, ct: ct);

        // Assert
        exit.Should().Be(3);
        console.Stderr.Should().Be(kind == "unset" ? expected : "zyggy: configuration error: " + path + expected);
        console.Stdout.Should().BeEmpty();
        _harness.Hooks.Calls.Should().BeEmpty("the checkout was never touched, nothing signed");
    }

    [Fact]
    public async Task Context_SignWithMissing_ExitThree()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string nodeJson = _harness.WriteNodeJson(CliHarness.HomeLaptop, _harness.Bus.CloneDir, "laptop-nosign", omitSignWith: true);

        // Act
        var (exit, console) = await _harness.RunAsync(["context", Fact], nodeJson, ct: ct);

        // Assert
        exit.Should().Be(3);
        console.Stderr.Should().Be("zyggy: configuration error: signWith missing\n");
    }

    [Fact]
    public async Task Context_KeyAbsentFromStore_ExitSixHintNothingCommitted()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string nodeJson = _harness.WriteNodeJson(CliHarness.HomeLaptop, _harness.Bus.CloneDir, "laptop-nokey", secretsRoot: _harness.Scratch.File("empty-secrets"));
        int commits = await _harness.CommitCountOnMainAsync(ct);

        // Act
        var (exit, console) = await _harness.RunAsync(["context", Fact], nodeJson, ct: ct);

        // Assert
        exit.Should().Be(6);
        console.Stderr.Should().Be("zyggy: signing key acme/1 is not in the secret store; run: zyggy secret set hmac/1 --hex\n");
        (await _harness.CommitCountOnMainAsync(ct)).Should().Be(commits);
        (await _harness.PorcelainAsync(_harness.Bus.CloneDir, ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Context_StoreUnavailable_ExitSixOneLine()
    {
        // Act
        var ct = TestContext.Current.CancellationToken;
        var (exit, console) = await _harness.RunAsync(["context", Fact], _harness.HomeLaptopNodeJson, more: s => s.AddSingleton<ISecretStore>(new ThrowingSecretStore()), ct: ct);

        // Assert
        exit.Should().Be(6, console.Stderr);
        console.Stderr.Should().Be("zyggy: secret store unavailable: credential-manager: 1312 No such logon session\n");
    }

    [Fact]
    public async Task Context_LocalPathNotARepository_ExitThree()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string notARepo = _harness.Scratch.File("not-a-repo");
        Directory.CreateDirectory(notARepo);
        string nodeJson = _harness.WriteNodeJson(CliHarness.HomeLaptop, notARepo, "laptop-norepo");

        // Act
        var (exit, console) = await _harness.RunAsync(["context", Fact], nodeJson, ct: ct);

        // Assert
        exit.Should().Be(3);
        console.Stderr.Should().Be("zyggy: configuration error: bus.localPath is not a git repository\n");
    }

    [Fact]
    public async Task Context_CheckoutOnAnotherBranch_ExitThree()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await _harness.Bus.RunGitAsync(_harness.Bus.CloneDir, ["switch", "-c", "other"], ct);
        try
        {
            // Act
            var (exit, console) = await _harness.RunAsync(["context", Fact], _harness.HomeLaptopNodeJson, ct: ct);

            // Assert
            exit.Should().Be(3);
            console.Stderr.Should().Be("zyggy: configuration error: checkout is on 'other', expected 'main'\n");
        }
        finally
        {
            await _harness.Bus.RunGitAsync(_harness.Bus.CloneDir, ["switch", "main"], ct);
        }
    }

    [Fact]
    public async Task Context_NoTenantDirectory_ExitThree()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var foreign = new BusRepoFixture(TenantId.Parse("other-org"));
        await foreign.InitializeAsync();
        try
        {
            await foreign.SeedTenantAsync(TenantId.Parse("other-org"), [CliHarness.HomeLaptop], ct);
            string nodeJson = _harness.WriteNodeJson(CliHarness.HomeLaptop, foreign.CloneDir, "laptop-foreign");

            // Act
            var (exit, console) = await _harness.RunAsync(["context", Fact], nodeJson, ct: ct);

            // Assert
            exit.Should().Be(3);
            console.Stderr.Should().Be("zyggy: configuration error: no tenants/acme/ directory in bus.localPath\n");
        }
        finally
        {
            await foreign.DisposeAsync();
        }
    }

    [Fact]
    public async Task Context_PullFails_ExitSixNothingWrittenWorkingCopyAsItWas()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string porcelainBefore = await _harness.PorcelainAsync(_harness.Bus.CloneDir, ct);
        _harness.Bus.DeleteBare();

        // Act
        var (exit, console) = await _harness.RunAsync(["context", Fact], _harness.HomeLaptopNodeJson, ct: ct);

        // Assert
        exit.Should().Be(6);
        console.Stderr.Should().StartWith("zyggy: context failed at pull:");
        console.Stdout.Should().BeEmpty();
        string folder = Path.Combine(_harness.Bus.CloneDir, "tenants", "acme", "nodes", "home-laptop", "context");
        (Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.md") : []).Should().BeEmpty();
        (await _harness.PorcelainAsync(_harness.Bus.CloneDir, ct)).Should().Be(porcelainBefore);
        SecretSweep.AssertAbsent([console.Stderr]);
    }

    [Fact]
    public async Task Context_EveryPushRefused_ExitSevenSuffixCommitLocal_NextWritePushesIt()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        _harness.Hooks.Override = spec => HookedProcessRunner.SubCommand(spec) == "push"
            ? new Zyggy.Core.Processes.ProcessResult(1, string.Empty, "! [rejected] main -> main (fetch first)", false, false, false, TimeSpan.Zero)
            : null;
        int commits = await _harness.CommitCountOnMainAsync(ct);

        // Act
        var (exit, console) = await _harness.RunAsync(["context", Fact], _harness.HomeLaptopNodeJson, ct: ct);

        // Assert
        exit.Should().Be(7);
        string[] lines = console.Stdout.Split('\n');
        lines[0].Should().EndWith(" (committed locally, not yet pushed: the next bus write on this machine pushes it)");
        lines[1].Should().Be($"- [stated] {Today}: {Fact}");
        (await _harness.Bus.RunGitAsync(_harness.Bus.CloneDir, ["log", "-1", "--format=%s"], ct)).Trim().Should().StartWith("acme context ");
        (await _harness.CommitCountOnMainAsync(ct)).Should().Be(commits);
        _harness.Hooks.Calls.Count(c => HookedProcessRunner.SubCommand(c) == "push").Should().Be(4);
        (EnvelopeId firstId, string firstPath) = Published(console.Stdout);

        // Act: the next bus write pushes it
        _harness.Hooks.Override = null;
        var (secondExit, second) = await _harness.RunAsync(["context", "Another fact"], _harness.HomeLaptopNodeJson, ct: ct);

        // Assert
        secondExit.Should().Be(0, second.Stderr);
        (await _harness.Bus.ExistsOnMainAsync(firstPath, ct)).Should().BeTrue(firstId.Value);
        (await _harness.Bus.ExistsOnMainAsync(Published(second.Stdout).Path, ct)).Should().BeTrue();
        (await _harness.Bus.RevListCountAsync("origin/main..HEAD", ct)).Should().Be(0);
    }

    [Fact]
    public async Task Context_ThisMachineIsCentral_ExitTwoUseMemoryRemember_NothingTouched()
    {
        // Act
        var ct = TestContext.Current.CancellationToken;
        var (exit, console) = await _harness.RunAsync(["context", Fact], _harness.CentralNodeJson, ct: ct);

        // Assert
        exit.Should().Be(2);
        console.Stderr.Should().Be("zyggy: to: this machine is central; use zyggy memory remember\n");
        console.Stdout.Should().BeEmpty();
        _harness.Hooks.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Context_HooksOff_ExitZeroNothingOnStdoutNothingWritten()
    {
        // Arrange: hooks off wins before node.json and the patterns are even looked at
        var ct = TestContext.Current.CancellationToken;
        int commits = await _harness.CommitCountOnMainAsync(ct);
        var env = new Dictionary<string, string?>(StringComparer.Ordinal) { ["ZYGGY_HOOKS"] = "off" };

        // Act
        var (exit, console) = await _harness.RunAsync(["context", Fact], _harness.HomeLaptopNodeJson, extraEnvironment: env, ct: ct);
        var (exitBad, bad) = await _harness.RunAsync(["context", Fact], _harness.Scratch.File("no-such-node.json"), extraEnvironment: env, withPatterns: false, ct: ct);

        // Assert
        exit.Should().Be(0);
        console.Stdout.Should().BeEmpty();
        console.Stderr.Should().BeEmpty();
        exitBad.Should().Be(0);
        bad.Stdout.Should().BeEmpty();
        bad.Stderr.Should().BeEmpty();
        (await _harness.CommitCountOnMainAsync(ct)).Should().Be(commits);
        _harness.Hooks.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Context_ConfigurationErrorWinsOverArgumentError()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string nodeJson = _harness.WriteNodeJson(CliHarness.HomeLaptop, _harness.Bus.CloneDir, "laptop-nosign2", omitSignWith: true);

        // Act
        var (exit, _) = await _harness.RunAsync(["context", "--scope", "people/marie", Fact], nodeJson, ct: ct);

        // Assert
        exit.Should().Be(3);
    }

    [Fact]
    public async Task Context_MemoryTreeBesideCheckoutByteIdentical_NoPrincipalVariableNeeded()
    {
        // Arrange: a memory tree the verb must never read or write; no ZYGGY_MEMORY_ROOT / ZYGGY_TENANT / ZYGGY_USER / ZYGGY_TIMEZONE
        var ct = TestContext.Current.CancellationToken;
        string memory = _harness.Scratch.File("memory");
        string alice = Path.Combine(memory, "acme", "alice");
        Directory.CreateDirectory(Path.Combine(alice, "inbox"));
        Directory.CreateDirectory(Path.Combine(alice, "daily"));
        await File.WriteAllTextAsync(Path.Combine(alice, "profile.md"), "---\nname: alice\n---\n- [stated] 2026-10-27: Alice builds the platform.\n", ct);
        await File.WriteAllTextAsync(Path.Combine(alice, "inbox", "remember-2026-10-27.md"), "- [stated] 2026-10-27: Marie prefers tea\n", ct);
        await File.WriteAllTextAsync(Path.Combine(alice, "daily", "2026-10-27.md"), "- [observed] 09:15 session 1a2b3c4d: planning\n", ct);
        var before = TreeFingerprint.Of(memory);
        var scratchBefore = Directory.EnumerateFiles(_harness.Scratch.Path, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList();
        var env = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ZYGGY_MEMORY_ROOT"] = null,
            ["ZYGGY_TENANT"] = null,
            ["ZYGGY_USER"] = null,
            ["ZYGGY_TIMEZONE"] = null,
        };

        // Act
        var (exit, console) = await _harness.RunAsync(["context", Fact], _harness.HomeLaptopNodeJson, extraEnvironment: env, ct: ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        TreeFingerprint.Of(memory).Should().Equal(before);
        Directory.EnumerateFiles(_harness.Scratch.Path, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Should().Equal(scratchBefore, "the only new files are inside the bus clone");
    }

    [Fact]
    public async Task Context_EveryOutputAndLogLine_NoKeyNoRefusedText()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;

        // Act
        var (exit, verbose) = await _harness.RunAsync(["context", "--verbose", Fact], _harness.HomeLaptopNodeJson, ct: ct);
        var (refusedExit, refused) = await _harness.RunAsync(["context", "--verbose", "token " + AwsKey], _harness.HomeLaptopNodeJson, ct: ct);
        // A credential in the remote URL must reach no output. Built from parts: the quoted whole reads as a mail address to the hygiene sweep.
        string remoteWithUserInfo = "https://x:y" + "@example.invalid/acme-org/acme-bus.git";
        await _harness.Bus.RunGitAsync(_harness.Bus.CloneDir, ["remote", "set-url", "origin", remoteWithUserInfo], ct);
        var (pullExit, pull) = await _harness.RunAsync(["context", Fact], _harness.HomeLaptopNodeJson, ct: ct);

        // Assert
        exit.Should().Be(0, verbose.Stderr);
        refusedExit.Should().Be(5);
        pullExit.Should().Be(6);
        var texts = new List<string?> { verbose.Stdout, verbose.Stderr, refused.Stdout, refused.Stderr, pull.Stdout, pull.Stderr };
        texts.AddRange(_harness.Hooks.Calls.SelectMany(SecretSweep.TextsOf));
        SecretSweep.AssertAbsent(texts);
        texts.Should().NotContain(t => t != null && t.Contains(AwsKey, StringComparison.Ordinal));
        pull.Stderr.Should().StartWith("zyggy: context failed at pull:").And.NotContain("x:y@", "git itself omits the user information, and RedactedDetail would strip it otherwise");
    }

    [Theory]
    [InlineData("--stop-hook")]
    [InlineData("--flush")]
    public async Task Context_StopHookOrFlushWithFact_ExitTwoMutuallyExclusive(string mode)
    {
        // Act
        var ct = TestContext.Current.CancellationToken;
        var (exit, console) = await _harness.RunAsync(["context", mode, Fact], _harness.HomeLaptopNodeJson, ct: ct);
        var (bothExit, both) = await _harness.RunAsync(["context", "--stop-hook", "--flush"], _harness.HomeLaptopNodeJson, ct: ct);

        // Assert
        exit.Should().Be(2);
        console.Stderr.Should().Be("zyggy: mode: --stop-hook, --flush and a fact are mutually exclusive\n");
        bothExit.Should().Be(2);
        both.Stderr.Should().Be("zyggy: mode: --stop-hook, --flush and a fact are mutually exclusive\n");
        _harness.Hooks.Calls.Should().BeEmpty();
    }

    private static (EnvelopeId Id, string Path) Published(string stdout)
    {
        Match line = PublishedLine().Match(stdout);
        line.Success.Should().BeTrue(stdout);
        return (EnvelopeId.Parse(line.Groups["id"].Value), line.Groups["path"].Value);
    }

    [GeneratedRegex(@"^context (?<id>[0-9A-HJKMNP-TV-Z]{26}) to central \((?<path>\S+)\)")]
    private static partial Regex PublishedLine();
}
