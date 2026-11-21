using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Integration.Cli;

/// <summary>
/// Spec 59 slice 11 (AC-14) through the real <c>zyggy</c> verbs: a draft written for the owner with <c>zyggy memory draft</c> is kept as
/// <c>drafts/&lt;date&gt;-&lt;slug&gt;.md</c> in Central's memory repository and appears on the owner's waiting list
/// (<c>zyggy work --waiting</c>, and the count at Central's session start); a secret-shaped draft is refused and nothing is kept.
/// </summary>
public sealed class DraftsTests : IAsyncLifetime
{
    private CliHarness _harness = null!;

    private Dictionary<string, string?> _memory = null!;

    private string Principal => Path.Combine(_harness.Scratch.File("memory"), "acme", "owner");

    public async ValueTask InitializeAsync()
    {
        _harness = await CliHarness.CreateAsync(CancellationToken.None);
        Directory.CreateDirectory(Principal);
        _memory = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ZYGGY_MEMORY_ROOT"] = _harness.Scratch.File("memory"),
            ["ZYGGY_TENANT"] = "acme",
            ["ZYGGY_USER"] = "owner",
            ["ZYGGY_TIMEZONE"] = "UTC",
        };
    }

    public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

    [Fact]
    public async Task Draft_ThenWaitingOnCentral_ListsTheDraftFile()
    {
        // Arrange
        var (draftExit, draft) = await DraftAsync("Dear client,\nthe offer follows.\n", "Mail to client");
        draftExit.Should().Be(0, draft.Stderr);
        string name = draft.Stdout["drafted: ".Length..].TrimEnd('\n');

        // Act
        var (exit, console) = await _harness.RunAsync(["work", "--waiting"], _harness.CentralNodeJson, extraEnvironment: _memory, ct: TestContext.Current.CancellationToken);

        // Assert
        new
        {
            Exit = exit,
            Kept = File.Exists(Path.Combine(Principal, name)),
            Listed = console.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Single().StartsWith("draft, central, " + name + ", ", StringComparison.Ordinal),
        }.Should().BeEquivalentTo(new { Exit = 0, Kept = true, Listed = true }, console.Stdout + console.Stderr);
    }

    [Fact]
    public async Task Draft_ThenSessionStartOnCentral_CountsIt()
    {
        // Arrange
        await DraftAsync("text\n", "Post for the blog");
        string session = _harness.Scratch.File("desk");
        Directory.CreateDirectory(session);

        // Act
        var (exit, console) = await _harness.RunAsync(
            ["work", "--session-start"], _harness.CentralNodeJson, stdin: HookJson.Stop(null, cwd: session), extraEnvironment: _memory, ct: TestContext.Current.CancellationToken);

        // Assert
        new { Exit = exit, console.Stdout }.Should().BeEquivalentTo(new { Exit = 0, Stdout = "1 item(s) wait for you: zyggy work --waiting\n" }, console.Stderr);
    }

    [Fact]
    public async Task WaitingOnALaptop_DraftsOfCentralNotListed()
    {
        // Arrange
        await DraftAsync("text\n", "Mail to client");

        // Act
        var (exit, console) = await _harness.RunAsync(["work", "--waiting"], _harness.HomeLaptopNodeJson, extraEnvironment: _memory, ct: TestContext.Current.CancellationToken);

        // Assert
        new { Exit = exit, console.Stdout }.Should().BeEquivalentTo(new { Exit = 0, Stdout = "nothing waits for you\n" }, console.Stderr);
    }

    [Fact]
    public async Task Draft_WithASecret_RefusedAndNothingKept()
    {
        // Act
        var (exit, console) = await DraftAsync("the key AKIAABCDEFGHIJKLMNOP\n", "Mail to client");

        // Assert
        new { Exit = exit, Drafts = Directory.Exists(Path.Combine(Principal, "drafts")) }.Should().BeEquivalentTo(new { Exit = 2, Drafts = false }, console.Stderr);
    }

    private Task<(int Exit, VerbConsole Console)> DraftAsync(string body, string title) =>
        _harness.RunAsync(["memory", "draft", "--title", title], _harness.CentralNodeJson, stdin: body, extraEnvironment: _memory, ct: TestContext.Current.CancellationToken);
}
