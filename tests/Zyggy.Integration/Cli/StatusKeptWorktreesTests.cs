using Zyggy.Core.Health;
using Zyggy.Core.Jobs;
using Zyggy.Core.Tenancy;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Cli;

/// <summary>
/// Spec 61 slice 9, AC-29 (the listing half): <c>zyggy status</c> through the verb prints <c>kept worktrees: &lt;n&gt;</c> and one line per
/// kept folder (<c>&lt;id&gt; &lt;path&gt;: &lt;why&gt;</c>) from the list the node's clean-up wrote; nothing when none is kept.
/// </summary>
public sealed class StatusKeptWorktreesTests : IAsyncLifetime
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private CliHarness _harness = null!;

    private StubHealthServer _health = null!;

    public async ValueTask InitializeAsync()
    {
        _harness = await CliHarness.CreateAsync(CancellationToken.None);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var green = new NodeHealth(Zyggy.Cli.Commands.BusHost.InformationalVersion(), "acme", "home-laptop", now, null, now, "0123456789ab", 0, 0, null, 4999, null, true, null, null, null);
        _health = new StubHealthServer(NodeHealthJson.Serialize(green));
    }

    public async ValueTask DisposeAsync()
    {
        await _health.DisposeAsync();
        await _harness.DisposeAsync();
    }

    [Fact]
    public async Task Status_TwoKeptWorktrees_CountAndOneLineEach()
    {
        // Arrange
        string nodeDir = _harness.NodeDirOf("home-laptop");
        string first = Path.Combine(nodeDir, "worktrees", "acme", "01M8000000000000000000000C");
        string second = Path.Combine(nodeDir, "worktrees", "acme", "01M8000000000000000000000D");
        LeftoverWorktrees.WriteKept(nodeDir, Acme, [
            new KeptWorktree("01M8000000000000000000000C", first, "push: ! [rejected] (non-fast-forward)"),
            new KeptWorktree("01M8000000000000000000000D", second, "project not found"),
        ]);

        // Act
        var (exit, console) = await StatusAsync();

        // Assert
        exit.Should().Be(0, console.Stdout + console.Stderr);
        console.Stdout.Should().Contain(
            "\n        kept worktrees: 2\n"
            + "          01M8000000000000000000000C " + first + ": push: ! [rejected] (non-fast-forward)\n"
            + "          01M8000000000000000000000D " + second + ": project not found\n");
    }

    [Fact]
    public async Task Status_NothingKept_NoKeptLine()
    {
        // Act
        var (exit, console) = await StatusAsync();

        // Assert
        exit.Should().Be(0, console.Stdout + console.Stderr);
        console.Stdout.Should().NotContain("kept worktrees");
    }

    private Task<(int Exit, Core.Tests.Infrastructure.VerbConsole Console)> StatusAsync() =>
        _harness.RunAsync(["status"], _harness.HomeLaptopNodeJson, extraEnvironment: new Dictionary<string, string?> { ["ZYGGY_HEALTH_URL"] = _health.Url.ToString() }, ct: TestContext.Current.CancellationToken);
}
