using System.Text.Json;

using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Memory;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Core.Tests.Memory;

namespace Zyggy.Core.Tests.Hub;

public sealed class HubServerRememberTests
{
    private const string FrontMatter =
        "---\nname: remember 2026-10-28\ndescription: facts stated by the owner on 2026-10-28 (remember skill)\nupdated: 2026-10-28\n---\n";

    public static TheoryData<string, string> UsageErrors() => new()
    {
        { """{"fact":""}""", "refused: empty fact" },
        { """{"fact":" \t\r\n "}""", "refused: empty fact" },
        { JsonSerializer.Serialize(new Dictionary<string, string> { ["fact"] = new('a', 1001) }), "refused: fact longer than 1000 characters" },
        { """{"fact":"x","scope":"people/marie"}""", "refused: --scope must be general, project:<name> or machine" },
        { """{"fact":"x","scope":"project:"}""", "refused: invalid project name in --scope" },
        { """{"fact":"x","scope":"project:Has Space"}""", "refused: invalid project name in --scope" },
    };

    [Fact]
    public async Task Remember_Fact_AppendsStatedLineAndAnswersRememberedPathAndLine()
    {
        // Arrange
        using var tree = new MemoryTree();
        await using var hub = await HubHarness.StartAsync(tree);

        // Act
        var result = await hub.CallAsync("remember", """{"fact":"Marie prefers tea"}""");

        // Assert
        var inbox = tree.Full("inbox/remember-2026-10-28.md");
        result.IsError.Should().NotBe(true);
        HubHarness.TextOf(result).Should().Be($"remembered: {inbox}\n- [stated] 2026-10-28: Marie prefers tea\n");
        File.ReadAllText(inbox).Should().Be(FrontMatter + "- [stated] 2026-10-28: Marie prefers tea\n");
    }

    [Fact]
    public async Task Remember_ResultText_ByteIdenticalToRememberVerbStdout()
    {
        // Arrange — two trees, the same clock and zone: the Hub on one, the CLI verb on the other.
        const string Fact = "The Zyggy repository is private";
        using var hubTree = new MemoryTree();
        using var verbTree = new MemoryTree();
        await using var hub = await HubHarness.StartAsync(hubTree);
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ZYGGY_MEMORY_ROOT"] = verbTree.Root,
            ["ZYGGY_TENANT"] = "acme",
            ["ZYGGY_USER"] = "alice",
            ["ZYGGY_TIMEZONE"] = "Europe/Brussels",
            ["ZYGGY_SECRET_PATTERNS"] = Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt"),
        };
        var console = new VerbConsole();

        // Act
        var text = HubHarness.TextOf(await hub.CallAsync("remember", $$"""{"fact":"{{Fact}}","scope":"project:zyggy"}"""));
        var exit = await new RememberVerb(environment, new FakeTimeProvider(HubHarness.Now), id => id == "Europe/Brussels" ? HubHarness.Brussels : TimeZoneInfo.FindSystemTimeZoneById(id))
            .RunAsync(["--scope", "project:zyggy", "--", Fact], console.Io, CancellationToken.None);

        // Assert
        exit.Should().Be(0);
        text.Should().Be(console.Stdout.Replace(verbTree.Root, hubTree.Root, StringComparison.Ordinal));
        File.ReadAllBytes(hubTree.Full("inbox/remember-2026-10-28.md")).Should().Equal(File.ReadAllBytes(verbTree.Full("inbox/remember-2026-10-28.md")));
    }

    [Theory]
    [InlineData("machine", "- [stated] 2026-10-28 (machine): a fact")]
    [InlineData("project:zyggy", "- [stated] 2026-10-28 (project:zyggy): a fact")]
    [InlineData("general", "- [stated] 2026-10-28: a fact")]
    public async Task Remember_ScopeMachineAndProject_HintInLine(string scope, string line)
    {
        // Arrange
        using var tree = new MemoryTree();
        await using var hub = await HubHarness.StartAsync(tree);

        // Act
        var result = await hub.CallAsync("remember", $$"""{"fact":"a fact","scope":"{{scope}}"}""");

        // Assert
        HubHarness.TextOf(result).Should().EndWith("\n" + line + "\n");
        File.ReadAllLines(tree.Full("inbox/remember-2026-10-28.md"))[^1].Should().Be(line);
    }

    [Fact]
    public async Task Remember_LocalDateFromZone_LateEveningRollsToNextDay()
    {
        // Arrange — 23:30 UTC is 01:30 the next day at +02:00.
        using var tree = new MemoryTree();
        await using var hub = await HubHarness.StartAsync(tree, now: new DateTimeOffset(2026, 10, 28, 23, 30, 0, TimeSpan.Zero));

        // Act
        var text = HubHarness.TextOf(await hub.CallAsync("remember", """{"fact":"late fact"}"""));

        // Assert
        text.Should().EndWith("\n- [stated] 2026-10-29: late fact\n");
        File.Exists(tree.Full("inbox/remember-2026-10-29.md")).Should().BeTrue();
    }

    [Fact]
    public async Task Remember_ExistingFile_KeepsLinesRewritesUpdated()
    {
        // Arrange
        using var tree = new MemoryTree((
            "inbox/remember-2026-10-28.md",
            "---\nname: remember 2026-10-28\ndescription: facts stated by the owner on 2026-10-28 (remember skill)\nupdated: 2026-10-01\n---\n- [stated] 2026-10-28: earlier fact\n"));
        await using var hub = await HubHarness.StartAsync(tree);

        // Act
        await hub.CallAsync("remember", """{"fact":"later fact"}""");

        // Assert
        File.ReadAllText(tree.Full("inbox/remember-2026-10-28.md")).Should().Be(
            FrontMatter + "- [stated] 2026-10-28: earlier fact\n- [stated] 2026-10-28: later fact\n");
    }

    [Theory]
    [MemberData(nameof(SecretPatternsTests.SecretSamples), MemberType = typeof(SecretPatternsTests))]
    public async Task Remember_SecretSample_IsErrorNamesPatternFactNowhereInboxUnchanged(string name, string sample)
    {
        // Arrange
        using var tree = new MemoryTree();
        await using var hub = await HubHarness.StartAsync(tree);

        // Act
        var result = await hub.CallAsync("remember", Fact(sample));

        // Assert
        result.IsError.Should().BeTrue();
        var text = HubHarness.TextOf(result);
        text.Should().Be($"refused: matches secret pattern {name}");
        text.Should().NotContain(sample);
        hub.Diagnostics.Should().NotContain(sample);
        Directory.Exists(tree.Full("inbox")).Should().BeFalse();
        Directory.EnumerateFiles(tree.Root, "*", SearchOption.AllDirectories)
            .Where(file => File.ReadAllText(file).Contains(sample, StringComparison.Ordinal))
            .Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(SecretPatternsTests.BenignSamples), MemberType = typeof(SecretPatternsTests))]
    public async Task Remember_BenignSample_Appended(string sample)
    {
        // Arrange
        using var tree = new MemoryTree();
        await using var hub = await HubHarness.StartAsync(tree);

        // Act
        var result = await hub.CallAsync("remember", Fact(sample));

        // Assert
        result.IsError.Should().NotBe(true);
        File.ReadAllLines(tree.Full("inbox/remember-2026-10-28.md"))[^1].Should().Be("- [stated] 2026-10-28: " + sample);
    }

    [Theory]
    [MemberData(nameof(UsageErrors))]
    public async Task Remember_EmptyOverLongOrBadScope_IsErrorRefusedMessage_NothingWritten(string arguments, string expected)
    {
        // Arrange
        using var tree = new MemoryTree();
        await using var hub = await HubHarness.StartAsync(tree);

        // Act
        var result = await hub.CallAsync("remember", arguments);

        // Assert
        result.IsError.Should().BeTrue();
        HubHarness.TextOf(result).Should().Be(expected);
        Directory.Exists(tree.Full("inbox")).Should().BeFalse();
    }

    [Theory]
    [InlineData("""{"fact":"x","tag":"observed"}""")]
    [InlineData("""{"fact":"x","source":"a session"}""")]
    [InlineData("""{"scope":"machine"}""")]
    [InlineData("""{"fact":7}""")]
    public async Task Remember_TagSourceOrIllTypedArgument_IsErrorInvalidArguments(string arguments)
    {
        // Arrange
        using var tree = new MemoryTree();
        await using var hub = await HubHarness.StartAsync(tree);

        // Act
        var result = await hub.CallAsync("remember", arguments);

        // Assert
        result.IsError.Should().BeTrue();
        HubHarness.TextOf(result).Should().Be("refused: invalid arguments");
        hub.Diagnostics.Should().BeEmpty();
        Directory.Exists(tree.Full("inbox")).Should().BeFalse();
    }

    [Theory]
    [InlineData("""{"fact":"a fact","tenant":"globex"}""")]
    [InlineData("""{"fact":"a fact","user":"bob"}""")]
    public async Task Remember_TenantOrUser_IsErrorRefusedOneStderrLine_NothingWritten(string arguments)
    {
        // Arrange
        using var tree = new MemoryTree();
        await using var hub = await HubHarness.StartAsync(tree);

        // Act
        var result = await hub.CallAsync("remember", arguments);

        // Assert
        result.IsError.Should().BeTrue();
        HubHarness.TextOf(result).Should().Be("refused: tenant and user are never request fields");
        hub.Diagnostics.Should().Be("hub: refused: request named a principal\n");
        Directory.Exists(tree.Full("inbox")).Should().BeFalse();
    }

    [Fact]
    public async Task Remember_HooksOff_NotStoredNotError_NothingWritten_GetContextStillAnswers()
    {
        // Arrange
        using var tree = HubServerGetContextTests.Tree();
        var before = TreeFingerprint.Of(tree.Root);
        await using var hub = await HubHarness.StartAsync(tree, hooksOff: true);

        // Act
        var remember = await hub.CallAsync("remember", """{"fact":"Marie prefers tea"}""");
        var context = await hub.CallAsync("get_context", """{"topic":"zyggy"}""");

        // Assert
        remember.IsError.Should().NotBe(true);
        HubHarness.TextOf(remember).Should().Be("not stored: hooks are off (unattended run)");
        TreeFingerprint.Of(tree.Root).Should().Equal(before);
        context.IsError.Should().NotBe(true);
        HubHarness.TextOf(context).Should().Contain("## business/areas/zyggy.md");
    }

    [Fact]
    public async Task Remember_SameFactTwice_TwoLines()
    {
        // Arrange
        using var tree = new MemoryTree();
        await using var hub = await HubHarness.StartAsync(tree);

        // Act
        await hub.CallAsync("remember", """{"fact":"same fact"}""");
        await hub.CallAsync("remember", """{"fact":"same fact"}""");

        // Assert
        File.ReadAllLines(tree.Full("inbox/remember-2026-10-28.md")).Count(line => line == "- [stated] 2026-10-28: same fact").Should().Be(2);
    }

    [Fact]
    public async Task Remember_NoTempFileSurvives()
    {
        // Arrange
        using var tree = new MemoryTree();
        await using var hub = await HubHarness.StartAsync(tree);

        // Act
        await hub.CallAsync("remember", """{"fact":"kept one"}""");
        await hub.CallAsync("remember", Fact("AKIAABCDEFGHIJKLMNOP"));
        await hub.CallAsync("remember", """{"fact":"kept two"}""");

        // Assert
        Directory.EnumerateFileSystemEntries(tree.Full("inbox")).Select(Path.GetFileName).Should().Equal("remember-2026-10-28.md");
    }

    private static string Fact(string fact) => JsonSerializer.Serialize(new Dictionary<string, string> { ["fact"] = fact });
}
