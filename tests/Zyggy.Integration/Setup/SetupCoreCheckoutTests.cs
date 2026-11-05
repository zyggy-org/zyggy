using Zyggy.Core.Setup;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Setup;

/// <summary>
/// Spec 42 AC-18 (and AC-21 for this item), plan 42 Step 4: <c>zyggy setup</c> in process against a local bare <c>zyggy-core</c> stand-in with
/// real git — clone, already up to date, fast-forward, dirty left as is, and exit 6 for an unreachable remote, a taken folder and another
/// <c>origin</c>. Never GitHub, never a prompt, never a credential helper, never <c>claude</c>.
/// </summary>
public sealed class SetupCoreCheckoutTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Setup_CoreAbsent_ClonedOnMain_SkillsAndPatternsPresent_LineDoneCloned()
    {
        // Arrange
        await using SetupHarness harness = await ReadyAsync();
        string head = await harness.Core.HeadAsync(Ct);

        // Act
        (int exit, VerbConsole console) = await harness.RunAsync(harness.Args(), Ct);

        // Assert
        exit.Should().Be(SetupExit.Ok, console.Stdout);
        CoreLine(console).Should().Be($"setup: zyggy-core: done - cloned {harness.CoreRemote} at {head[..12]}");
        (await harness.Core.GitAsync(harness.CorePath, ["symbolic-ref", "--short", "HEAD"], Ct)).Should().Be("main");
        (await harness.Core.GitAsync(harness.CorePath, ["config", "--get", "remote.origin.url"], Ct)).Should().Be(harness.CoreRemote);
        CoreTemplateFixture.SeededFiles.Select(f => File.Exists(Path.Combine(harness.CorePath, f))).Should().AllBeEquivalentTo(true);
    }

    [Fact]
    public async Task Setup_SecondRun_AlreadyUpToDate_NoNewCommit_NoFileRewritten()
    {
        // Arrange
        await using SetupHarness harness = await ReadyAsync();
        await harness.RunAsync(harness.Args(), Ct);
        string head = await harness.Core.HeadAsync(Ct);
        IReadOnlyDictionary<string, (string Content, DateTime LastWriteUtc)> before = harness.WorkingFiles();

        // Act
        (int exit, VerbConsole console) = await harness.RunAsync(harness.Args(), Ct);

        // Assert
        exit.Should().Be(SetupExit.Ok, console.Stdout);
        CoreLine(console).Should().Be($"setup: zyggy-core: already done - already up to date ({harness.CorePath})");
        Lines(console).Should().OnlyContain(l => l.Contains(": already done", StringComparison.Ordinal));
        (await harness.Core.HeadAsync(Ct)).Should().Be(head);
        (await harness.Core.GitAsync(harness.CorePath, ["rev-parse", "HEAD"], Ct)).Should().Be(head);
        harness.WorkingFiles().Should().Equal(before);
    }

    [Fact]
    public async Task Setup_RemoteAdvanced_FastForwarded_NewFileAppears()
    {
        // Arrange
        await using SetupHarness harness = await ReadyAsync();
        await harness.RunAsync(harness.Args(), Ct);
        string advanced = await harness.Core.PushChangeAsync("node/skills/remember/notes.md", "A new placeholder line.\n", Ct);

        // Act
        (int exit, VerbConsole console) = await harness.RunAsync(harness.Args(), Ct);

        // Assert
        exit.Should().Be(SetupExit.Ok, console.Stdout);
        CoreLine(console).Should().Be($"setup: zyggy-core: done - fast-forwarded to {advanced[..12]}");
        File.ReadAllText(Path.Combine(harness.CorePath, "node", "skills", "remember", "notes.md")).Should().Be("A new placeholder line.\n");
    }

    [Fact]
    public async Task Setup_CoreDirty_LeftAsIsDirty_FileKept_ExitZero()
    {
        // Arrange
        await using SetupHarness harness = await ReadyAsync();
        await harness.RunAsync(harness.Args(), Ct);
        await harness.Core.PushChangeAsync("node/README.md", "Moved on upstream.\n", Ct);
        string readme = Path.Combine(harness.CorePath, "node", "README.md");
        File.WriteAllText(readme, "A local edit.\n");

        // Act
        (int exit, VerbConsole console) = await harness.RunAsync(harness.Args(), Ct);

        // Assert
        exit.Should().Be(SetupExit.Ok, console.Stdout);
        CoreLine(console).Should().Be("setup: zyggy-core: left as is - dirty working copy");
        File.ReadAllText(readme).Should().Be("A local edit.\n");
        harness.Hooks.Calls.Select(HookedProcessRunner.SubCommand).Should().NotContain(["pull", "fetch", "reset", "stash", "checkout", "clean"]);
    }

    [Fact]
    public async Task Setup_RemoteUnreachable_ExitSix_FirstStderrLineInTheDetail()
    {
        // Arrange
        await using SetupHarness harness = await ReadyAsync();
        string nowhere = new Uri(harness.Scratch.File("nowhere.git")).AbsoluteUri;

        // Act
        (int exit, VerbConsole console) = await harness.RunAsync(harness.Args(coreRemote: nowhere), Ct);

        // Assert
        exit.Should().Be(SetupExit.Incomplete, console.Stdout);
        CoreLine(console).Should().StartWith("setup: zyggy-core: failed - fatal: ");
        Directory.Exists(harness.CorePath).Should().BeFalse();
        Lines(console).Should().Contain(l => l.StartsWith("setup: signing key acme/1: already done", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Setup_CorePathIsAPlainFolder_ExitSix_Untouched()
    {
        // Arrange
        await using SetupHarness harness = await ReadyAsync();
        Directory.CreateDirectory(harness.CorePath);
        string notes = Path.Combine(harness.CorePath, "notes.txt");
        File.WriteAllText(notes, "not a checkout\n");

        // Act
        (int exit, VerbConsole console) = await harness.RunAsync(harness.Args(), Ct);

        // Assert
        exit.Should().Be(SetupExit.Incomplete, console.Stdout);
        CoreLine(console).Should().Be($"setup: zyggy-core: failed - {harness.CorePath} is not a git checkout");
        Directory.GetFileSystemEntries(harness.CorePath).Should().Equal(notes);
        File.ReadAllText(notes).Should().Be("not a checkout\n");
    }

    [Fact]
    public async Task Setup_CorePathHasAnotherOrigin_ExitSix_Untouched()
    {
        // Arrange
        await using SetupHarness harness = await ReadyAsync();
        await harness.RunAsync(harness.Args(), Ct);
        const string Fork = "https://example.invalid/fork/zyggy-core.git";
        await harness.Core.GitAsync(harness.CorePath, ["remote", "set-url", "origin", Fork], Ct);
        string head = await harness.Core.GitAsync(harness.CorePath, ["rev-parse", "HEAD"], Ct);
        harness.Hooks.Calls.Clear();

        // Act
        (int exit, VerbConsole console) = await harness.RunAsync(harness.Args(), Ct);

        // Assert
        exit.Should().Be(SetupExit.Incomplete, console.Stdout);
        CoreLine(console).Should().Be($"setup: zyggy-core: failed - {harness.CorePath} has origin {Fork}, expected {harness.CoreRemote}");
        harness.Hooks.Calls.Select(HookedProcessRunner.SubCommand).Should().NotContain(["pull", "fetch", "clone"]);
        (await harness.Core.GitAsync(harness.CorePath, ["rev-parse", "HEAD"], Ct)).Should().Be(head);
    }

    [Fact]
    public async Task Setup_EveryGitCall_NoPromptVariables_NoClaudeProcess()
    {
        // Arrange: a clone run, then an update run
        await using SetupHarness harness = await ReadyAsync();
        await harness.RunAsync(harness.Args(), Ct);

        // Act
        await harness.RunAsync(harness.Args(), Ct);

        // Assert
        harness.Hooks.Calls.Should().NotBeEmpty();
        harness.Hooks.Calls.Should().AllSatisfy(c =>
        {
            Path.GetFileName(c.FileName).Should().NotContainEquivalentOf("claude");
            c.FileName.Should().Be("git");
            c.Environment["GIT_TERMINAL_PROMPT"].Should().Be("0");
            c.Environment["GCM_INTERACTIVE"].Should().Be("never");
            c.Environment["LC_ALL"].Should().Be("C");
            c.Environment.Should().NotContainKey("GIT_ASKPASS");
            c.Arguments.Should().NotContain(a => a.Contains("credential", StringComparison.OrdinalIgnoreCase));
        });
    }

    private static async Task<SetupHarness> ReadyAsync()
    {
        SetupHarness harness = await SetupHarness.CreateAsync(Ct);
        harness.WriteNodeJson(harness.OldSpikeJson(signWith: "acme/1"));
        return harness;
    }

    private static string[] Lines(VerbConsole console) => console.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static string CoreLine(VerbConsole console) => Lines(console).Single(l => l.StartsWith("setup: zyggy-core: ", StringComparison.Ordinal));
}
