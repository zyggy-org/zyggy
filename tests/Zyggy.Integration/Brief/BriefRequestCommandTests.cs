using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Brief;

/// <summary><c>zyggy brief request</c> from the built binary, as the session's Bash tool calls it: the request file the path unit watches.</summary>
public sealed class BriefRequestCommandTests : IDisposable
{
    private readonly ScratchDirectory _scratch = new();

    public void Dispose() => _scratch.Dispose();

    private string State => Path.Combine(_scratch.Path, "state");

    private Dictionary<string, string?> Env() => new()
    {
        ["HOME"] = Path.Combine(_scratch.Path, "home"),
        ["ZYGGY_STATE_DIR"] = State,
        ["ZYGGY_TIMEZONE"] = "UTC",
        ["XDG_CONFIG_HOME"] = Path.Combine(_scratch.Path, "home", ".config"),
        ["XDG_STATE_HOME"] = Path.Combine(_scratch.Path, "home", ".local", "state"),
    };

    private Task<ZyggyRun> Brief(params string[] args) =>
        ZyggyCli.RunAsync(["brief", .. args], Env(), null, _scratch.Path, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Request_WritesRequestFileAndPrints()
    {
        // Act
        var run = await Brief("request");

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        run.Stdout.Should().Be("brief requested\n");
        run.Stderr.Should().BeEmpty();
        File.Exists(Path.Combine(State, "brief.request")).Should().BeTrue();
    }

    [Fact]
    public async Task Request_WithArgument_ExitFour()
    {
        // Act
        var run = await Brief("request", "now");

        // Assert
        run.ExitCode.Should().Be(4);
        run.Stderr.Should().Be("brief: request takes no argument (usage: zyggy brief request)\n");
        File.Exists(Path.Combine(State, "brief.request")).Should().BeFalse();
    }
}
