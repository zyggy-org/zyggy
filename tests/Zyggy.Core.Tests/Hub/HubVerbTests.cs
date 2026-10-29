using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Hub;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Hub;

public sealed class HubVerbTests : IDisposable
{
    private readonly MemoryTree _tree = new();
    private readonly Dictionary<string, string?> _environment;

    public HubVerbTests() => _environment = new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        ["ZYGGY_MEMORY_ROOT"] = _tree.Root,
        ["ZYGGY_TENANT"] = "acme",
        ["ZYGGY_USER"] = "alice",
        ["ZYGGY_TIMEZONE"] = "Europe/Brussels",
        ["ZYGGY_SECRET_PATTERNS"] = Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt"),
    };

    public void Dispose() => _tree.Dispose();

    [Fact]
    public async Task Run_Proxy_ExitFourNamesDeliverable21_NothingOnStdout()
    {
        // Act
        var (exit, console) = await RunAsync("--proxy");

        // Assert
        exit.Should().Be(HubVerb.Usage);
        console.Stdout.Should().BeEmpty();
        console.Stderr.Should().Be("hub: --proxy is deliverable 21; zyggy hub takes no arguments\n");
    }

    [Theory]
    [InlineData("foo")]
    [InlineData("--port")]
    [InlineData("get_context")]
    public async Task Run_AnyOtherArgument_ExitFourTakesNoArguments(string argument)
    {
        // Act
        var (exit, console) = await RunAsync(argument);

        // Assert
        exit.Should().Be(4);
        console.Stdout.Should().BeEmpty();
        console.Stderr.Should().Be("hub: takes no arguments\n");
    }

    [Fact]
    public async Task Run_ConfigurationError_ExitThreeOneStderrLine_NothingOnStdout()
    {
        // Arrange
        _environment.Remove("ZYGGY_TENANT");

        // Act
        var (exit, console) = await RunAsync();

        // Assert
        exit.Should().Be(HubVerb.Configuration);
        console.Stdout.Should().BeEmpty();
        console.Stderr.Should().Be("hub: configuration error: ZYGGY_TENANT is not set\n");
    }

    [Fact]
    public async Task Run_PatternsMissing_ExitThree_NeverServes()
    {
        // Arrange
        var missing = Path.Combine(_tree.Root, "missing.txt");
        _environment["ZYGGY_SECRET_PATTERNS"] = missing;

        // Act
        var (exit, console) = await RunAsync();

        // Assert
        exit.Should().Be(3);
        console.Stdout.Should().BeEmpty();
        console.Stderr.Should().Be($"hub: configuration error: {missing} is missing\n");
    }

    [Fact]
    public async Task Run_UsageBeforeConfiguration_ExitFourEvenWhenUnconfigured()
    {
        // Arrange
        _environment.Remove("ZYGGY_TENANT");

        // Act
        var (exit, console) = await RunAsync("extra");

        // Assert
        exit.Should().Be(4);
        console.Stderr.Should().Be("hub: takes no arguments\n");
    }

    [Fact]
    public void ExitCodes_ZeroThreeFour()
    {
        // Assert
        HubVerb.StdinClosed.Should().Be(0);
        HubVerb.Configuration.Should().Be(3);
        HubVerb.Usage.Should().Be(4);
    }

    private async Task<(int Exit, VerbConsole Console)> RunAsync(params string[] args)
    {
        var console = new VerbConsole();
        var exit = await new HubVerb(
                _environment,
                new FakeTimeProvider(HubHarness.Now),
                id => id == "Europe/Brussels" ? HubHarness.Brussels : TimeZoneInfo.FindSystemTimeZoneById(id))
            .RunAsync(args, console.Io, CancellationToken.None);
        return (exit, console);
    }
}
